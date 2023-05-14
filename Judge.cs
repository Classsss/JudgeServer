using Microsoft.CodeAnalysis;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace JudgeServer {
    public class Judge {
        // 채점 폴더가 생성될 기본 경로
        private const string SUBMIT_FOLDER_PATH = "docker";

        // 도커 이미지 이름
        private const string IMAGE_NAME = "leehayoon/judge";

        // signalR을 보낼 CLASS HUB의 주소
        private const string CLASSHUB_REALTIMESUBMITHUB = "https://classhub.azurewebsites.net/realtimesubmithub";
        
        /// <summary>
        /// 채점 요청을 받은 코드를 채점함.
        /// </summary>
        /// <param name="request">ClassHub에서 요청한 정보가 담긴 객체</param>
        /// <returns>채점 결과 정보가 담긴 객체</returns>
        public static async Task<JudgeResult> JudgeCodeAsync(JudgeRequest request) {
            // 반환할 채점 정보를 저장하는 객체   
            JudgeResult result = new JudgeResult();

            // 교수 정답 코드
            string correctCode;
            // 학생 제출 코드
            string submitCode;
            // 교수 코드의 언어
            string correctCodeLanguage;
            // 학생 코드의 언어
            string submitCodeLanguage;
            // 입력 테스트 케이스
            List<string> inputCases;
            // 실행 시간(ms) 제한
            double executionTimeLimit;
            // 메모리 사용량(KB) 제한
            long memoryUsageLimit;
            // 교수 정답 리스트
            List<string> correctResult = new List<string>();
            // 학생 정답 리스트
            List<string> submitResult = new List<string>();

            // 채점 요청 객체에서 정답 코드, 제출 코드, 코드 언어 타입, 입력 테스트 케이스, 실행시간 및 메모리 사용량 제한을 받아온다.
            GetJudgeData(in request, out correctCode, out submitCode, out correctCodeLanguage, out submitCodeLanguage, out inputCases, out executionTimeLimit, out memoryUsageLimit);

            // 채점 요청별로 사용할 유니크한 정답 폴더명 및 유저 채점 폴더명
            string correctFolderName, submitFolderName;

            // 정답 폴더, 유저 채점 폴더, 입력케이스, 컴파일 에러 메시지, 런타임 에러 메시지, 코드 실행 결과, 실행 시간과 메모리 사용량이 저장되는 경로들
            string correctFolderPath, submitFolderPath, correctInputFilePath, submitInputFilePath, compileErrorFilePath, runtimeErrorFilePath, correctResultFilePath, submitResultFilePath, statFilePath;

            // 정답 폴더, 유저 채점 폴더를 생성하고 내부에 생성되는 파일들의 경로를 받아옴
            CreateSubmitFolder(in correctCodeLanguage, in submitCodeLanguage, out correctFolderName, out submitFolderName, out correctFolderPath, out submitFolderPath, out correctInputFilePath, out submitInputFilePath, out compileErrorFilePath, out runtimeErrorFilePath, out correctResultFilePath, out submitResultFilePath, out statFilePath);

            // 코드를 언어에 맞는 확장자를 가지는 파일로 저장
            string correctCodeFilePath, submitCodeFilePath;
            CreateCodeFile(in correctFolderPath, in submitFolderPath, in correctCode, in submitCode, in correctCodeLanguage, in submitCodeLanguage, out correctCodeFilePath, out submitCodeFilePath);

            // Docker Hub의 이미지 태그
            string correctImageTag = correctCodeLanguage;
            string submitImageTag = submitCodeLanguage;

            // 정답 채점과 유저 채점을 위한 Docker client 초기화
            // TODO : dockerClient, volumeMapping이 null이 아닐 때 예외처리 필요
            var correctDockerTuple = await InitDockerClientAsync(correctImageTag, correctFolderPath, correctFolderName);
            var submitDockerTuple = await InitDockerClientAsync(submitImageTag, submitFolderPath, submitFolderName);

            DockerClient? correctDockerClient = correctDockerTuple.Item1;
            Dictionary<string, string>? correctVolumeMapping = correctDockerTuple.Item2;

            DockerClient? submitDockerClient = submitDockerTuple.Item1;
            Dictionary<string, string>? submitVolumeMapping = submitDockerTuple.Item2;

            // signalR을 이용해 실시간으로 클라이언트에 채점 진행 현황 전달
            await using var signalRConnection = new HubConnectionBuilder()
                 .WithUrl("https://localhost:7182/realtimesubmithub")
                 .Build();
            await signalRConnection.StartAsync();

            // 총 케이스 횟수
            int totalCaseCount = inputCases.Count();

            // 테스트 케이스들의 평균 실행 시간과 메모리 사용량
            double avgExecutionTime = 0;
            long avgMemoryUsage = 0;

            for (int caseCount = 0; caseCount < totalCaseCount; caseCount++) {
                // 클라이언트에게 채점 진행 현황과 SubmitId 전달
                double percent = Math.Truncate((caseCount + 1) / (float)request.InputCases.Count * 100);
                if (percent == 100) { percent = 99; }
                await signalRConnection.InvokeAsync("PercentageWatcher", percent, request.SubmitId);

                // 입력 케이스를 정답 폴더와 유저 채점 폴더에 파일로 저장
                File.WriteAllText(correctInputFilePath, inputCases[caseCount]);
                File.WriteAllText(submitInputFilePath, inputCases[caseCount]);

                // 정답 채점과 유저 채점을 수행하는 컨테이너 구동
                await RunDockerContainerAsync(correctDockerClient, correctVolumeMapping, correctImageTag, correctFolderName);
                await RunDockerContainerAsync(submitDockerClient, submitVolumeMapping, submitImageTag, submitFolderName);

                // 정답 채점과 유저 채점 중 컴파일 에러가 발생했는지 체크
                if (IsOccuredCompileError(in compileErrorFilePath, in correctFolderName, in correctCodeLanguage, ref result)) { break; }
                if (IsOccuredCompileError(in compileErrorFilePath, in submitFolderName, in submitCodeLanguage, ref result)) { break; }

                // 정답 채점과 유저 채점 중 런타임 에러가 발생했는지 체크
                if (IsOccuredRuntimeError(in runtimeErrorFilePath, in correctFolderName, in correctCodeLanguage, ref result)) { break; }
                if (IsOccuredRuntimeError(in runtimeErrorFilePath, in submitFolderName, in submitCodeLanguage, ref result)) { break; }

                // 실행 시간과 메모리 사용량
                double executionTime;
                long memoryUsage;

                // 유저 제출 코드의 실행 시간, 메모리 사용량 측정 값을 받아옴
                GetStats(in statFilePath, out executionTime, out memoryUsage);
                Console.WriteLine($"실행 시간 측정 - limit : {executionTimeLimit} / acutal : {executionTime}");
                Console.WriteLine($"메모리 사용량 측정 - limit : {memoryUsageLimit} / acutal : {memoryUsage}");

                // 유저 제출 코드에서 시간 초과가 발생했는지 체크
                if (IsExceededTimeLimit(in executionTime, in executionTimeLimit, ref result)) { break; }

                // 유저 제출 코드에서 메모리 초과가 발생했는지 체크
                if (IsExceededMemoryLimit(in memoryUsage, in memoryUsageLimit, ref result)) { break; }

                // 평균 실행 시간 및 평균 메모리 사용량 계산
                avgExecutionTime += executionTime;
                avgMemoryUsage += memoryUsage;

                // 정답 코드와 유저 코드의 실행 결과를 실행 결과 리스트들에 저장
                correctResult.Add((File.Exists(correctResultFilePath) ? File.ReadAllText(correctResultFilePath) : "").Trim());
                submitResult.Add((File.Exists(submitResultFilePath) ? File.ReadAllText(submitResultFilePath) : "").Trim());

                // 실행 결과와 정답을 비교
                if (!JudgeTestCase(in correctResult, in submitResult, in caseCount, ref result)) { break; }

                // 테스트 케이스에서 사용하는 파일 초기화
                InitFile(in correctInputFilePath, in correctResultFilePath);
                InitFile(in submitInputFilePath, in submitResultFilePath);
            }

            // 채점 폴더 삭제
            DeleteSubmitFolder(in correctFolderPath);
            DeleteSubmitFolder(in submitFolderPath);

            // 채점 진행 현황 전달을 위한 연결 종료
            await signalRConnection.StopAsync();

            // 모든 테스트 케이스를 수행하면 결과를 저장해 JudgeResult 객체 반환
            return GetJudgeResult(in totalCaseCount, ref result, ref avgExecutionTime, ref avgMemoryUsage);
        }

        /// <summary>
        /// 파라미터로 전달받은 JudgeRequest 모델 객체에서 입출력 테스트 케이스, 실행 시간 제한, 메모리 사용량 제한 값을 받아옴
        /// </summary>
        /// <param name="request">파라미터로 전달받은 JudgeRequest 모델 객체</param>
        /// <param name="correctCode">정답 코드</param>
        /// <param name="submitCode">채점할 코드</param>
        /// <param name="correctCodeLanguage">정답 코드의 언어</param>
        /// <param name="submitCodeLanguage">학생 코드의 언어</param>
        /// <param name="inputCases">입력 테스트 케이스</param>
        /// <param name="executionTimeLimit">실행 시간 제한</param>
        /// <param name="memoryUsageLimit">메모리 사용량 제한</param>
        private static void GetJudgeData(in JudgeRequest request, out string correctCode, out string submitCode, out string correctCodeLanguage, out string submitCodeLanguage, out List<string> inputCases, out double executionTimeLimit, out long memoryUsageLimit) {
            correctCode = request.CorrectCode;
            submitCode = request.SubmitCode;
            correctCodeLanguage = request.CorrectCodeLanguage;
            submitCodeLanguage = request.SubmitCodeLanguage;
            inputCases = request.InputCases;
            executionTimeLimit = request.ExecutionTimeLimit;
            memoryUsageLimit = request.MemoryUsageLimit;
        }

        /// <summary>
        /// 채점 폴더를 생성하고 그 안에서 사용할 파일들의 경로를 초기화
        /// </summary>
        /// <param name=correctCodeLanguage">정답 코드의 언어</param>
        /// <param name=submitCodeLanguage">제출 코드의 언어</param>
        /// <param name="correctFolderName">정답 코드의 채점 폴더명/param>
        /// <param name="submitFolderName">제출 코드의 채점 폴더명/param>
        /// <param name="correctFolderPath">정답 코드의 채점 폴더의 경로</param>
        /// <param name="submitFolderPath">제출 코드의 채점 폴더의 경로</param>
        /// <param name="correctInputFilePath">정답 코드의 입력 케이스가 저장되는 경로</param>
        /// <param name="submitInputFilePath">제출 코드의 입력 케이스가 저장되는 경로</param>
        /// <param name="compileErrorFilePath">컴파일 에러 메시지가 저장되는 경로</param>
        /// <param name="runtimeErrorFilePath">런타임 에러 메시지가 저장되는 경로</param>
        /// <param name="correctResultFilePath">정답 코드의 결과가 저장되는 경로</param>
        /// <param name="submitResultFilePath">제출 코드의 결과가 저장되는 경로</param>
        /// <param name="statFilePath">실행 시간과 메모리 사용량이 저장되는 경로</param>
        private static void CreateSubmitFolder(in string correctCodeLanguage, in string submitCodeLanguage, out string correctFolderName, out string submitFolderName, out string correctFolderPath, out string submitFolderPath, out string correctInputFilePath, out string submitInputFilePath, out string compileErrorFilePath, out string runtimeErrorFilePath, out string correctResultFilePath, out string submitResultFilePath, out string statFilePath) {
            // 128비트 크기의 유니크한 GUID로 정답 폴더명 및 유저 채점 폴더명 생성
            correctFolderName = Guid.NewGuid().ToString();
            submitFolderName = Guid.NewGuid().ToString();

            // 정답 폴더 및 유저 채점 폴더 경로
            correctFolderPath = Path.Combine(Directory.GetCurrentDirectory(), SUBMIT_FOLDER_PATH, correctCodeLanguage, correctFolderName);
            submitFolderPath = Path.Combine(Directory.GetCurrentDirectory(), SUBMIT_FOLDER_PATH, submitCodeLanguage, submitFolderName);

            // 폴더가 존재하지 않는 경우에만 폴더를 생성합니다.
            if (!Directory.Exists(correctFolderPath)) {
                Directory.CreateDirectory(correctFolderPath);
                Console.WriteLine($"폴더가 생성되었습니다: {correctFolderPath}");
            }
            if (!Directory.Exists(submitFolderPath)) {
                Directory.CreateDirectory(submitFolderPath);
                Console.WriteLine($"폴더가 생성되었습니다: {submitFolderPath}");
            }

            // 입력 케이스가 저장되는 경로
            correctInputFilePath = Path.Combine(correctFolderPath, "input.txt");
            submitInputFilePath = Path.Combine(submitFolderPath, "input.txt");

            // 컴파일 에러 메시지의 경로
            compileErrorFilePath = Path.Combine(submitFolderPath, "compileError.txt");

            // 런타임 에러 메시지의 경로
            runtimeErrorFilePath = Path.Combine(submitFolderPath, "runtimeError.txt");

            // 결과가 저장되는 경로
            correctResultFilePath = Path.Combine(correctFolderPath, "result.txt");
            submitResultFilePath = Path.Combine(submitFolderPath, "result.txt");

            // 실행 시간과 메모리 사용량이 저장되는 경로
            statFilePath = Path.Combine(submitFolderPath, "stat.txt");
        }

        /// <summary>
        /// 전달받은 코드를 언어에 맞는 파일로 생성
        /// </summary>
        /// <param name="correctFolderPath">정답 코드의 채점 폴더의 경로</param>
        /// <param name="submitFolderPath">제출 코드의 채점 폴더의 경로</param>
        /// <param name="correctCode">정답 코드</param>
        /// <param name="submitCode">제출 코드</param>
        /// <param name="correctCodeLanguage">정답 코드의 언어</param>
        /// <param name="submitCodeLanguage">제출 코드의 언어</param>
        /// <param name="correctCodeFilePath">정답 코드 파일의 경로</param>
        /// <param name="submitCodeFilePath">제출 코드 파일의 경로</param>
        private static void CreateCodeFile(in string correctFolderPath, in string submitFolderPath, in string correctCode, in string submitCode, in string correctCodeLanguage, in string submitCodeLanguage, out string correctCodeFilePath, out string submitCodeFilePath) {
            // 정답 코드 파일명 설정
            // 파이썬 코드 파일명
            if (correctCodeLanguage == "python") {
                correctCodeFilePath = Path.Combine(correctFolderPath, "Main.py");
            }
            // C# 코드 파일명
            else if (correctCodeLanguage == "csharp") {
                correctCodeFilePath = Path.Combine(correctFolderPath, "Main.cs");

                // C# 코드 실행을 위한 .csproj 파일을 상위 폴더에서 복사해옴
                string parentDirectory = Directory.GetParent(correctFolderPath).FullName;
                string sourceProjFilePath = Path.Combine(parentDirectory, "Main.csproj");
                string destProjFilePath = Path.Combine(correctFolderPath, "Main.csproj");
                File.Copy(sourceProjFilePath, destProjFilePath, true);
            }
            // 나머지 언어 파일명
            else {
                correctCodeFilePath = Path.Combine(correctFolderPath, $"Main.{correctCodeLanguage}");
            }

            // 유저 코드 파일명 설정
            // 파이썬 코드 파일명
            if (submitCodeLanguage == "python") {
                submitCodeFilePath = Path.Combine(submitFolderPath, "Main.py");
            }
            // C# 코드 파일명
            else if (submitCodeLanguage == "csharp") {
                submitCodeFilePath = Path.Combine(submitFolderPath, "Main.cs");

                // C# 코드 실행을 위한 .csproj 파일을 상위 폴더에서 복사해옴
                string parentDirectory = Directory.GetParent(submitFolderPath).FullName;
                string sourceProjFilePath = Path.Combine(parentDirectory, "Main.csproj");
                string destProjFilePath = Path.Combine(submitFolderPath, "Main.csproj");
                File.Copy(sourceProjFilePath, destProjFilePath, true);
            }
            // 나머지 언어 파일명
            else {
                submitCodeFilePath = Path.Combine(submitFolderPath, $"Main.{submitCodeLanguage}");
            }

            // 정답 코드 파일과 유저 코드 파일 생성
            File.WriteAllText(correctCodeFilePath, correctCode);
            File.WriteAllText(submitCodeFilePath, submitCode);
        }

        /// <summary>
        /// DockerClient를 생성하고 이미지를 빌드하여 컨테이너 생성을 준비하는 비동기 메소드
        /// </summary>
        /// <param name="imageTag">도커 이미지 태그</param>
        /// <param name="folderPath">채점 폴더 경로</param>
        /// <param name="folderName">채점 폴더 명</param>
        /// <returns>생성된 DockerClient와 volumeMapping을 ValueTuple로 반환</returns>
        private static async Task<ValueTuple<DockerClient?, Dictionary<string, string>?>> InitDockerClientAsync(string imageTag, string folderPath, string folderName) {
            // Docker client 생성
            DockerClient? dockerClient = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();

            // 도커 허브에서 이미지 다운로드
            await dockerClient.Images.CreateImageAsync(new ImagesCreateParameters {
                FromImage = IMAGE_NAME,
                Tag = imageTag
            }, new AuthConfig(), new Progress<JSONMessage>());

            // 볼륨 맵핑 - 로컬 폴더 : 컨테이너 내부 채점 폴더
            Dictionary<string, string>? volumeMapping = new Dictionary<string, string> { { folderPath, $"/app/{folderName}" } };

            // ValueTuple로 반환
            return (dockerClient, volumeMapping);
        }

        /// <summary>
        /// 빌드한 이미지로 컨테이너 생성, 실행, 제거를 수행하는 비동기 메소드
        /// </summary>
        /// <param name="dockerClient">도커 클라이언트</param>
        /// <param name="volumeMapping">로컬 디렉토리와 컨테이너 간의 볼륨 매핑</param>
        /// <param name="imageTag">도커 이미지 태그</param>
        /// <param name="folderName">채점 폴더명</param>
        /// <returns>비동기 작업 Task 반환</returns>
        private static async Task RunDockerContainerAsync(DockerClient? dockerClient, Dictionary<string, string>? volumeMapping, string imageTag, string folderName) {
            // 컨테이너 생성
            CreateContainerResponse? createContainerResponse = await dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters {
                Image = $"{IMAGE_NAME}:{imageTag}",
                // 환경 변수 설정
                Env = new List<string> { "DIR_NAME=" + folderName },
                // 볼륨 설정
                HostConfig = new HostConfig {
                    Binds = volumeMapping.Select(kv => $"{kv.Key}:{kv.Value}").ToList(),
                }
            });

            // 컨테이너 실행
            await dockerClient.Containers.StartContainerAsync(createContainerResponse.ID, new ContainerStartParameters());

            // 컨테이너 실행이 끝날때까지 대기
            await dockerClient.Containers.WaitContainerAsync(createContainerResponse.ID);

            // 컨테이너 종료 및 삭제
            await dockerClient.Containers.StopContainerAsync(createContainerResponse.ID, new ContainerStopParameters());
            await dockerClient.Containers.RemoveContainerAsync(createContainerResponse.ID, new ContainerRemoveParameters());
        }

        /// <summary>
        /// 에러 메시지에서 폴더명을 제거하여 반환
        /// </summary>
        /// <param name="originalMsg">원본 에러 메시지</param>
        /// <param name="folderName">채점 폴더명</param>
        /// <returns>채점 폴더명이 제거된 에러 메시지</returns>
        private static string GetMessageWithoutFolderName(in string originalMsg, in string folderName) {
            return originalMsg.Replace($"{folderName}/", "");
        }

        /// <summary>
        /// 컴파일 에러 메시지에서 필요없는 문장을 제거함
        /// </summary>
        /// <param name="originalMsg">원본 컴파일 에러 메시지</param>
        /// <param name="language">코드의 언어</param>
        /// <returns>편집된 컴파일 에러 메시지</returns>
        private static string ModifyCompileErrorMsg(in string originalMsg, in string language) {
            string modifiedMsg = originalMsg;
            switch (language) {
                case "csharp":
                    string[] cSharpLines = originalMsg.Split('\n');
                    cSharpLines = cSharpLines.Where((line, index) => (!(index >= 0 && index <= 2) && !(index >= 6 && index <= (cSharpLines.Length - 1)))).ToArray();
                    modifiedMsg = string.Join("\n", cSharpLines);
                    break;
                case "java":
                    string[] javaLines = originalMsg.Split('\n');
                    javaLines = javaLines.Where((line, index) => (index != (javaLines.Length - 1) && index != (javaLines.Length - 2))).ToArray();
                    modifiedMsg = string.Join("\n", javaLines);
                    break;
            }

            return modifiedMsg;
        }

        /// <summary>
        /// 런타임 에러 메시지에서 필요없는 문장을 제거함
        /// </summary>
        /// <param name="originalMsg">원본 런타임 에러 메시지</param>
        /// <param name="language">코드의 언어</param>
        /// <returns>편집된 런타임 에러 메시지</returns>
        private static string ModifyRuntimeErrorMsg(in string originalMsg, in string language) {
            string modifiedMsg = originalMsg;
            switch (language) {
                case "c":
                case "cpp":
                    string[] lines = originalMsg.Split('\n');
                    lines = lines.Where((line, index) => (index != 0 && index != 1)).ToArray();
                    modifiedMsg = string.Join("\n", lines);
                    break;
            }

            return modifiedMsg;
        }

        /// <summary>
        /// 컴파일 에러가 발생했는지 체크
        /// </summary>
        /// <param name="compileErrorFilePath">컴파일 에러 메시지가 저장되는 경로</param>
        /// <param name="folderName">채점 폴더명</param>
        /// <param name="language">코드의 언어</param>
        /// <param name="result">채점 결과를 저장하는 객체</param>
        /// <returns>컴파일 에러가 발생할 때 true</returns>
        private static bool IsOccuredCompileError(in string compileErrorFilePath, in string folderName, in string language, ref JudgeResult result) {
            // 컴파일 에러가 발생했는지 체크
            if (File.Exists(compileErrorFilePath)) {
                string errorMsg = File.ReadAllText(compileErrorFilePath);

                // 컴파일 에러 발생
                if (errorMsg.Length != 0) {
                    // 에러 메시지에서 폴더명 제거
                    errorMsg = GetMessageWithoutFolderName(in errorMsg, in folderName);
                    // 에러 메시지에서 필요없는 문장 제거
                    errorMsg = ModifyCompileErrorMsg(errorMsg, language);
                    Console.WriteLine("Compile Error Occured : " + errorMsg);

                    result.Result = JudgeResult.JResult.CompileError;
                    result.Message = errorMsg;
                    return true;
                }
            }

            // 컴파일 에러 미발생
            return false;
        }

        /// <summary>
        /// 런타임 에러가 발생했는지 체크
        /// </summary>
        /// <param name="runtimeErrorFilePath">런타임 에러 메시지가 저장되는 경로</param>
        /// <param name="folderName">채점 폴더명</param>
        /// <param name="language">코드의 언어</param>
        /// <param name="result">채점 결과를 저장하는 객체</param>
        /// <returns>런타임 에러가 발생할 때 true</returns>
        private static bool IsOccuredRuntimeError(in string runtimeErrorFilePath, in string folderName, in string language, ref JudgeResult result) {
            // 런타임 에러가 발생했는지 체크
            if (File.Exists(runtimeErrorFilePath)) {
                string errorMsg = File.ReadAllText(runtimeErrorFilePath);

                // 런타임 에러 발생
                if (errorMsg.Length != 0) {
                    // 에러 메시지에서 폴더명 제거
                    errorMsg = GetMessageWithoutFolderName(in errorMsg, in folderName);
                    // 에러 메시지에서 필요없는 문장 제거
                    errorMsg = ModifyRuntimeErrorMsg(errorMsg, language);
                    Console.WriteLine("Runtime Error Occured : " + errorMsg);

                    result.Result = JudgeResult.JResult.RuntimeError;
                    result.Message = errorMsg;
                    return true;
                }
            }

            // 런타임 에러 미발생
            return false;
        }

        /// <summary>
        /// 실행 시간, 메모리 사용량을 측정
        /// </summary>
        /// <param name="statFilePath">실행 시간, 메모리 사용량이 저장되는 경로</param>
        /// <param name="executionTime">실행 시간</param>
        /// <param name="memoryUsage">메모리 사용량</param>
        private static void GetStats(in string statFilePath, out double executionTime, out long memoryUsage) {
            // 실행 시간, 메모리 사용량이 측정됐는지 체크
            if (File.Exists(statFilePath)) {
                string[] statLines = File.ReadAllLines(statFilePath);

                // 올바르게 측정됐으면 실행 시간, 메모리 사용량만 2줄로 저장됨
                if (statLines.Length == 2) {
                    // 문자열을 숫자로 변환하여 사용
                    executionTime = double.Parse(statLines[0].Trim());

                    // TODO : 메모리 사용량 측정 구현
                    //memoryUsage = long.Parse(statLines[1].Trim());
                    memoryUsage = 0;

                    return;
                }
            }

            // 유효한 측정 값이 없을 때
            executionTime = 0;
            memoryUsage = 0;
        }

        /// <summary>
        /// 시간 초과가 발생했는지 체크
        /// </summary>
        /// <param name="executionTime">실행 시간</param>
        /// <param name="executionTimeLimit">실행 시간 제한</param>
        /// <param name="result">채점 결과를 저장하는 객체</param>
        /// <returns>시간 초과가 발생했으면 true</returns>
        private static bool IsExceededTimeLimit(in double executionTime, in double executionTimeLimit, ref JudgeResult result) {
            // 시간 초과 발생
            if (executionTime > executionTimeLimit) {
                Console.WriteLine("시간 초과 발생");

                result.Result = JudgeResult.JResult.TimeLimitExceeded;
                return true;
            }

            // 시간 초과 미발생
            return false;
        }

        /// <summary>
        /// 메모리 초과가 발생했는지 체크
        /// </summary>
        /// <param name="memoryUsage">메모리 사용량</param>
        /// <param name="memoryUsageLimit">메모리 사용량 제한</param>
        /// <param name="result">채점 결과를 저장하는 객체</param>
        /// <returns>메모리 초과가 발생할 때 true</returns>
        private static bool IsExceededMemoryLimit(in long memoryUsage, in long memoryUsageLimit, ref JudgeResult result) {
            // 메모리 초과 발생
            if (memoryUsage > memoryUsageLimit) {
                Console.WriteLine("메모리 초과 발생");

                result.Result = JudgeResult.JResult.MemoryLimitExceeded;
                return true;
            }

            // 메모리 초과 미발생
            return false;
        }

        /// <summary>
        /// 테스트 케이스를 수행한 실행 결과와 정답을 비교
        /// </summary>
        /// <param name="correctResult">정답 리스트</param>
        /// <param name="submitResult">유저 제출 코드 채점 결과 리스트</param> 
        /// <param name="caseCount">현재 테스트 케이스 순서, 0부터 시작됨</param> 
        /// <param name="result">채점 결과를 저장하는 객체</param> 
        /// <returns>테스트 케이스를 통과했을 때 true</returns>
        private static bool JudgeTestCase(in List<string> correctResult, in List<string> submitResult, in int caseCount, ref JudgeResult result) {
            // 출력 케이스와 결과 비교
            Console.WriteLine($"채점 - expected : {correctResult[caseCount]} / actual : {submitResult[caseCount]}");

            // 틀림
            if (correctResult[caseCount] != submitResult[caseCount]) {
                Console.WriteLine($"{caseCount + 1}번째 케이스 틀림");
                result.Result = JudgeResult.JResult.WrongAnswer;
                return false;
            }

            // 맞음
            Console.WriteLine($"{caseCount + 1}번째 케이스 통과");
            result.Result = JudgeResult.JResult.Accepted;
            return true;
        }

        /// <summary>
        /// 사용이 끝난 입력 케이스 파일과 결과 파일을 초기화
        /// </summary>
        /// <param name="inputFilePath">입력 케이스가 저장되는 경로</param>
        /// <param name="resultFilePath">결과가 저장되는 경로</param>
        private static void InitFile(in string inputFilePath, in string resultFilePath) {
            // 입력 파일 초기화
            if (File.Exists(inputFilePath)) {
                File.WriteAllText(inputFilePath, string.Empty);
            }

            // 정답 결과 파일 초기화
            if (File.Exists(resultFilePath)) {
                File.WriteAllText(resultFilePath, string.Empty);
            }
        }

        /// <summary>
        /// 채점 폴더를 삭제
        /// </summary>
        /// <param name="folderPath">채점 폴더 경로</param>
        private static void DeleteSubmitFolder(in string folderPath) {
            // 채점 폴더를 내부 파일까지 전부 삭제한다.
            if (Directory.Exists(folderPath)) {
                Directory.Delete(folderPath, true);
            }
        }

        /// <summary>
        /// 채점 결과에 맞게 JudgeResult 객체의 데이터를 채워 반환
        /// </summary>
        /// <param name="totalCaseCount">테스트 케이스의 총 개수</param>
        /// <param name="result">채점 결과가 저장되는</param>
        /// <param name="avgExecutionTime">테스트 케이스 평균 실행 시간</param>
        /// <param name="avgMemoryUsage">테스트 케이스 평균 메모리 사용량</param>
        /// <returns>채점 결과에 맞게 데이터가 채워진 JudgeResult 객체</returns>
        private static JudgeResult GetJudgeResult(in int totalCaseCount, ref JudgeResult result, ref double avgExecutionTime, ref long avgMemoryUsage) {
            // 테스트 케이스를 통과하지 못함
            if (result.Result != JudgeResult.JResult.Accepted) {
                return result;
            }

            // 모든 테스트 케이스를 통과

            // 평균 실행 시간, 메모리 사용량 계산
            avgExecutionTime /= totalCaseCount;
            avgMemoryUsage /= totalCaseCount;

            // 실행 시간, 메모리 사용량 데이터 저장
            result.ExecutionTime = avgExecutionTime;
            result.MemoryUsage = avgMemoryUsage;

            Console.WriteLine("모든 케이스 통과");
            Console.WriteLine("avgExecutionTime : " + avgExecutionTime);
            Console.WriteLine("avgMemoryUsage : " + avgMemoryUsage);

            return result;
        }
    }
}
