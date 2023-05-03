using Microsoft.CodeAnalysis;
using Docker.DotNet;
using Docker.DotNet.Models;
using Azure.Storage.Files.Shares;
using System.Text;
using Azure.Storage.Files.Shares.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JudgeServer {
    public class Judge {
        // 채점 폴더가 생성될 기본 경로
        private const string SUBMIT_FOLDER_PATH = "docker";

        // 도커 이미지 이름
        private const string IMAGE_NAME = "leehayoon/judge";

        // Azure Storage
        private const string connectionString = "DefaultEndpointsProtocol=https;AccountName=judgeserverstorage;AccountKey=g3U8N+1P6ScUvS1+woCbLQw+4DJYCT4G26cDb4k4sCBUXt/1Fx+LVwdlg6qlraT0RscFtrguV0d8+AStP1JW5w==;EndpointSuffix=core.windows.net";
        private const string shareName = "judge";

        private static ShareClient shareClient;

        static Judge() {
            // 스토리지 계정과 연결하고 파일 공유 클라이언트를 생성합니다.
            shareClient = new ShareClient(connectionString, shareName);

            // 파일 공유가 이미 존재하는지 확인하고, 없으면 생성합니다.
            if (!shareClient.Exists()) {
                shareClient.Create();
            }
        }

        private static void CreateDirectoryIfNotExists(in string directoryPath) {
            // 디렉토리 클라이언트를 생성하고 디렉토리를 생성합니다.
            ShareDirectoryClient directoryClient = shareClient.GetDirectoryClient(directoryPath);
            directoryClient.CreateIfNotExists();
        }

        private static async Task<string> ReadFile(string directoryPath, string fileName) {
            // 디렉토리 클라이언트 생성
            ShareDirectoryClient directoryClient = shareClient.GetDirectoryClient(directoryPath);

            // 파일 클라이언트 생성
            ShareFileClient fileClient = directoryClient.GetFileClient(fileName);

            // 파일 다운로드
            ShareFileDownloadInfo downloadInfo = await fileClient.DownloadAsync();

            // 다운로드한 파일의 내용 읽기
            using (StreamReader reader = new StreamReader(downloadInfo.Content)) {
                string readFileContent = await reader.ReadToEndAsync();
                return readFileContent; 
            }
        }

        private static async Task UploadFile(string filePath, string fileContent) {
            // 디렉토리 클라이언트를 생성하고 디렉토리를 생성합니다.
            ShareDirectoryClient directoryClient = shareClient.GetDirectoryClient(Path.GetDirectoryName(filePath));

            // 파일 클라이언트 생성 및 파일 업로드
            ShareFileClient fileClient = directoryClient.CreateFile(Path.GetFileName(filePath), fileContent.Length);
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(fileContent))) {
                await fileClient.UploadAsync(stream);
            }
        }

        private static async Task CopyFile(string sourceFilePath, string destFilePath) {
            // 복사의 원본 파일의 경로로 부터 DirectoryClient와 FileClient 생성
            ShareDirectoryClient sourceDirectoryClient = shareClient.GetDirectoryClient(Path.GetDirectoryName(sourceFilePath));
            ShareFileClient sourceFileClient = sourceDirectoryClient.GetFileClient(Path.GetFileName(sourceFilePath));

            // 복사된 파일이 저장될 경로로 부터 DirectoryClient와 FileClient 생성
            ShareDirectoryClient destDirectoryClient = shareClient.GetDirectoryClient(Path.GetDirectoryName(destFilePath));
            ShareFileClient destFileClient = destDirectoryClient.GetFileClient(Path.GetFileName(destFilePath));

            // 복사할 파일의 URL 가져오기
            Uri sourceFileUri = sourceFileClient.Uri;

            // 파일 복사
            await destFileClient.StartCopyAsync(sourceFileUri);
        }

        private static async Task DeleteDirectoryIfExists(string directoryPath) {
            ShareDirectoryClient directoryClient = shareClient.GetDirectoryClient(directoryPath);
            await directoryClient.DeleteIfExistsAsync();
        }

        /// <summary>
        /// 채점 요청을 받은 코드를 채점함.
        /// </summary>
        /// <param name="request">ClassHub에서 요청한 정보가 담긴 객체</param>
        /// <returns>채점 결과 정보가 담긴 객체</returns>
        public static async Task<JudgeResult> JudgeCodeAsync(JudgeRequest request, ILogger logger) {
            // 반환할 채점 정보를 저장하는 객체
            JudgeResult result = new JudgeResult();

            // 전달받은 코드
            string code;
            // 코드의 언어
            string language;
            // 입력 테스트 케이스
            List<string> inputCases;
            // 출력 테스트 케이스
            List<string> outputCases;
            // 실행 시간(ms) 제한
            double executionTimeLimit;
            // 메모리 사용량(KB) 제한
            long memoryUsageLimit;

            // 채점 DB에서 입출력 케이스, 실행 시간 제한, 메모리 사용량 제한을 받아옴
            GetJudgeData(in request, out code, out language, out inputCases, out outputCases, out executionTimeLimit, out memoryUsageLimit);

            // 채점 요청별로 사용할 유니크한 폴더명
            string folderName;

            // 유저 제출 폴더, 입력케이스, 컴파일 에러 메시지, 런타임 에러 메시지, 실행 결과, 실행 시간과 메모리 사용량이 저장되는 경로들
            string folderPath, inputFilePath, compileErrorFilePath, runtimeErrorFilePath, resultFilePath, statFilePath;

            // 채점 제출 폴더를 생성하고 내부에 생성되는 파일들의 경로를 받아옴
            CreateSubmitFolder(in language, out folderName, out folderPath, out inputFilePath, out compileErrorFilePath, out runtimeErrorFilePath, out resultFilePath, out statFilePath);

            // 코드를 언어에 맞는 형식을 가지는 파일로 저장
            string codeFilePath = await CreateCodeFile(folderPath, code, language);

            // Docker Hub에서의 이미지 태그
            string imageTag = language;

            // Docker client 초기화
            // TODO : dockerClient, volumeMapping이 null이 아닐 때 예외처리 필요
            DockerClient? dockerClient = await InitDockerClientAsync(imageTag, folderPath, folderName);

            // 테스트 케이스들의 평균 실행 시간과 메모리 사용량
            double avgExecutionTime = 0;
            long avgMemoryUsage = 0;

            // 케이스 횟수
            int caseCount = outputCases.Count();

            await UploadFile(inputFilePath, inputCases[0]);

            // 컨테이너 구동
            await RunDockerContainerAsync(dockerClient, imageTag, folderName);

            //// 테스트 케이스 수행
            //for (int i = 0; i < caseCount; i++) {
            //    // 입력 케이스를 파일로 저장
            //    await UploadFile(inputFilePath, inputCases[i]);

            //    // 컨테이너 구동
            //    await RunDockerContainerAsync(dockerClient, imageTag, folderName);

            //    // 컴파일 에러인지 체크
            //    if (IsOccuredCompileError(in compileErrorFilePath, in folderName, in language, ref result)) {
            //        break;
            //    }

            //    // 런타임 에러가 발생했는지 체크
            //    if (IsOccuredRuntimeError(in runtimeErrorFilePath, in folderName, in language, ref result)) {
            //        break;
            //    }

            //    // 실행 시간과 메모리 사용량
            //    double executionTime;
            //    long memoryUsage;

            //    // 실행 시간, 메모리 사용량 측정 값을 받아옴
            //    GetStats(in statFilePath, out executionTime, out memoryUsage);
            //    Console.WriteLine($"limit : {executionTimeLimit} / acutal : {executionTime}");

            //    // 시간 초과가 발생했는지 체크
            //    if (IsExceededTimeLimit(in executionTime, in executionTimeLimit, ref result)) {
            //        break;
            //    }

            //    // 메모리 초과가 발생했는지 체크
            //    if (IsExceededMemoryLimit(in memoryUsage, in memoryUsageLimit, ref result)) {
            //        break;
            //    }

            //    // 평균 실행 시간 및 메모리 사용량 계산
            //    avgExecutionTime += executionTime;
            //    avgMemoryUsage += memoryUsage;

            //    // 현재 진행 중인 테스트 케이스의 출력 케이스
            //    string outputCase = outputCases[i];

            //    // 실행 결과와 출력 케이스 비교
            //    if (!JudgeTestCase(in outputCase, in resultFilePath, ref result)) {
            //        break;
            //    }

            //    Console.WriteLine($"{i + 1}번째 케이스 통과");

            //    // 테스트 케이스에서 사용하는 파일 초기화
            //    InitFile(in inputFilePath, in resultFilePath);
            //}

            //// 채점 제출 폴더 삭제
            //DeleteSubmitFolder(in folderPath);

            //// 모든 테스트 케이스를 수행하면 결과를 저장해 JudgeResult 객체 반환
            //return GetJudgeResult(in caseCount, ref result, ref avgExecutionTime, ref avgMemoryUsage);

            return result;
        }

        /// <summary>
        /// 파라미터로 전달받은 JudgeRequest 모델 객체에서 입출력 테스트 케이스, 실행 시간 제한, 메모리 사용량 제한 값을 받아옴
        /// </summary>
        /// <param name="request">파라미터로 전달받은 JudgeRequest 모델 객체</param>
        /// <param name="code">채점할 코드</param>
        /// <param name="language">코드의 프로그래밍 언어</param>
        /// <param name="inputCases">입력 테스트 케이스</param>
        /// <param name="outputCases">출력 테스트 케이스</param>
        /// <param name="executionTimeLimit">실행 시간 제한</param>
        /// <param name="memoryUsageLimit">메모리 사용량 제한</param>
        private static void GetJudgeData(in JudgeRequest request, out string code, out string language, out List<string> inputCases, out List<string> outputCases, out double executionTimeLimit, out long memoryUsageLimit) {
            code = request.Code;
            language = request.Language;
            inputCases = request.InputCases;
            outputCases = request.OutputCases;
            executionTimeLimit = request.ExecutionTimeLimit;
            memoryUsageLimit = request.MemoryUsageLimit;
        }

        /// <summary>
        /// 채점 제출 폴더를 생성하고 그 안에서 사용할 파일들의 경로를 초기화
        /// </summary>
        /// <param name="language">코드의 언어</param>
        /// <param name="folderName">채점 제출 폴더명/param>
        /// <param name="folderPath">채점 제출 폴더의 경로</param>
        /// <param name="inputFilePath">입력 케이스가 저장되는 경로</param>
        /// <param name="compileErrorFilePath">컴파일 에러 메시지가 저장되는 경로</param>
        /// <param name="runtimeErrorFilePath">런타임 에러 메시지가 저장되는 경로</param>
        /// <param name="resultFilePath">결과가 저장되는 경로</param>
        /// <param name="statFilePath">실행 시간과 메모리 사용량이 저장되는 경로</param>
        private static void CreateSubmitFolder(in string language, out string folderName, out string folderPath, out string inputFilePath, out string compileErrorFilePath, out string runtimeErrorFilePath, out string resultFilePath, out string statFilePath) {
            // 128비트 크기의 유니크한 GUID로 폴더명 생성
            folderName = Guid.NewGuid().ToString();

            // 채점 제출 폴더 생성
            folderPath = Path.Combine(SUBMIT_FOLDER_PATH, language, folderName);
            CreateDirectoryIfNotExists(in folderPath);

            // 입력 케이스가 저장되는 경로
            inputFilePath = Path.Combine(folderPath, "input.txt");

            // 컴파일 에러 메시지의 경로
            compileErrorFilePath = Path.Combine(folderPath, "compileError.txt");

            // 런타임 에러 메시지의 경로
            runtimeErrorFilePath = Path.Combine(folderPath, "runtimeError.txt");

            // 결과가 저장되는 경로
            resultFilePath = Path.Combine(folderPath, "result.txt");

            // 실행 시간과 메모리 사용량이 저장되는 경로
            statFilePath = Path.Combine(folderPath, "stat.txt");
        }

        /// <summary>
        /// 전달받은 코드를 언어에 맞는 파일로 생성
        /// </summary>
        /// <param name="folderPath">채점 제출 폴더 경로</param>
        /// <param name="code">코드</param>
        /// <param name="language">코드의 언어</param>
        /// <param name="codeFilePath">코드 파일의 경로</param>
        private static async Task<string> CreateCodeFile(string folderPath, string code, string language) {
            string codeFilePath;

            // 파이썬의 경우 언어 이름과 파일 형식이 다름
            if (language == "python") {
                codeFilePath = Path.Combine(folderPath, "Main.py");
            } 
            // C#의 경우 언어 이름과 파일 형식이 다름
            else if (language == "csharp") {
                codeFilePath = Path.Combine(folderPath, "Main.cs");

                // C# 코드 실행을 위한 .csproj 파일을 상위 폴더에서 복사해옴
                string parentDirectory = Directory.GetParent(folderPath).FullName;
                string sourceProjFilePath = Path.Combine(parentDirectory, "Main.csproj");
                string destProjFilePath = Path.Combine(folderPath, "Main.csproj");

                await CopyFile(sourceProjFilePath, destProjFilePath);
            } 
            // 나머지 경우 언어 이름과 파일 형식이 동일함
            else {
                codeFilePath = Path.Combine(folderPath, $"Main.{language}");
            }
            await UploadFile(codeFilePath, code);

            return codeFilePath;
        }

        /// <summary>
        /// DockerClient를 생성하고 이미지를 빌드하여 컨테이너 생성을 준비하는 비동기 메소드
        /// </summary>
        /// <param name="imageTag">도커 이미지 태그</param>
        /// <param name="folderPath">채점 제출 폴더 경로</param>
        /// <param name="folderName">채점 제출 폴더 명</param>
        /// <returns>생성된 DockerClient와 volumeMapping을 ValueTuple로 반환</returns>
        private static async Task<DockerClient?> InitDockerClientAsync(string imageTag, string folderPath, string folderName) {
            // Docker client 생성
            var credentials = new AnonymousCredentials();
            DockerClient? dockerClient = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine"), credentials).CreateClient();

            // 이미지 다운로드
            await dockerClient.Images.CreateImageAsync(new ImagesCreateParameters {
                FromImage = IMAGE_NAME, Tag = imageTag
            }, new AuthConfig(), new Progress<JSONMessage>());

            // ValueTuple로 반환
            return dockerClient;
        }

        /// <summary>
        /// 빌드한 이미지로 컨테이너 생성, 실행, 제거를 수행하는 비동기 메소드
        /// </summary>
        /// <param name="dockerClient">도커 클라이언트</param>
        /// <param name="volumeMapping">컨테이너와의 볼륨 매핑</param>
        /// <param name="imageTag">도커 이미지 태그</param>
        /// <param name="folderName">채점 제출 폴더명</param>
        /// <returns>비동기 작업 Task 반환</returns>
        private static async Task RunDockerContainerAsync(DockerClient? dockerClient, string imageTag, string folderName) {

            // Azure File Share 정보
            string azureStorageAccountName = "judgeserverstorage";
            string azureStorageAccountKey = "g3U8N+1P6ScUvS1+woCbLQw+4DJYCT4G26cDb4k4sCBUXt/1Fx+LVwdlg6qlraT0RscFtrguV0d8+AStP1JW5w==";
            string azureFileShareName = "judge";
            string containerPath = Path.Combine("/app", folderName);

            // Azure File Share 볼륨 매핑
            var hostConfig = new HostConfig {
                Mounts = new List<Mount> {
                    new Mount {
                        Type = "volume",
                        Source = $"//{azureStorageAccountName}.file.core.windows.net/{azureFileShareName}",
                        Target = containerPath,
                        VolumeOptions = new VolumeOptions {
                            DriverConfig = new Driver {
                                Name = "azure_file",
                                Options = new Dictionary<string, string> {
                                    { "share_name", azureFileShareName },
                                    { "storage_account_name", azureStorageAccountName },
                                    { "storage_account_key", azureStorageAccountKey }
                                }
                            }
                        }
                    }
                }
            };

            CreateContainerResponse? createContainerResponse = await dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters {
                Image = $"{IMAGE_NAME}:{imageTag}",
                // 환경 변수 설정
                Env = new List<string> { "DIR_NAME=" + folderName },
                // 볼륨 설정
                HostConfig = hostConfig
            });

            // 컨테이너 생성
            //CreateContainerResponse? createContainerResponse = await dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters {
            //    Image = $"{IMAGE_NAME}:{imageTag}",
            //    // 환경 변수 설정
            //    Env = new List<string> { "DIR_NAME=" + folderName },
            //    // 볼륨 설정
            //    HostConfig = new HostConfig {
            //        Binds = volumeMapping.Select(kv => $"{kv.Key}:{kv.Value}").ToList(),
            //    }
            //});

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
        /// <param name="folderName">폴더명</param>
        /// <returns>폴더명이 제거된 에러 메시지</returns>
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
        /// <param name="result">채점 결과를 저장하는 객체</param>
        /// <returns>컴파일 에러가 발생할 때 true</returns>
        private static bool IsOccuredCompileError(in string compileErrorFilePath, in string folderName, in string language, ref JudgeResult result) {
            // 컴파일 에러 발생
            if (File.Exists(compileErrorFilePath)) {
                string errorMsg = File.ReadAllText(compileErrorFilePath);

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

            return false;
        }

        /// <summary>
        /// 런타임 에러가 발생했는지 체크
        /// </summary>
        /// <param name="runtimeErrorFilePath">런타임 에러 메시지가 저장되는 경로</param>
        /// <param name="result">채점 결과를 저장하는 객체</param>
        /// <returns>런타임 에러가 발생할 때 true</returns>
        private static bool IsOccuredRuntimeError(in string runtimeErrorFilePath, in string folderName, in string language, ref JudgeResult result) {
            // 런타임 에러가 발생했는지 체크
            if (File.Exists(runtimeErrorFilePath)) {
                string errorMsg = File.ReadAllText(runtimeErrorFilePath);

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

                    Console.WriteLine($"실행시간:{executionTime} 메모리 사용량:{memoryUsage}");

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
            // 시간 초과
            if (executionTime > executionTimeLimit) {
                Console.WriteLine($"[시간 초과] 실행시간:{executionTime} / 제한:{executionTimeLimit}");

                result.Result = JudgeResult.JResult.TimeLimitExceeded;
                return true;
            }

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
            // 메모리 초과
            if (memoryUsage > memoryUsageLimit) {
                Console.WriteLine($"[메모리 초과] 메모리 사용량:{memoryUsage} / 제한:{memoryUsageLimit}");

                result.Result = JudgeResult.JResult.MemoryLimitExceeded;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 테스트 케이스를 수행한 실행 결과와 정답을 비교
        /// </summary>
        /// <param name="outputCase">출력 테스트 케이스</param>
        /// <param name="resultFilePath">결과가 저장되는 경로</param>
        /// <param name="result">채점 결과가 저장되는 객체</param>
        /// <returns>테스트 케이스를 통과했을 때 true</returns>
        private static bool JudgeTestCase(in string outputCase, in string resultFilePath, ref JudgeResult result) {
            // 출력 케이스와 결과 비교
            string expectedOutput = outputCase;
            string actualOutput = (File.Exists(resultFilePath) ? File.ReadAllText(resultFilePath) : "").Trim();
            Console.WriteLine($"expected : {expectedOutput} / actual : {actualOutput}");

            // 틀림
            if (expectedOutput != actualOutput) {
                result.Result = JudgeResult.JResult.WrongAnswer;
                return false;
            }

            // 맞음
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

            // 결과 파일 초기화
            if (File.Exists(resultFilePath)) {
                File.WriteAllText(resultFilePath, string.Empty);
            }
        }

        /// <summary>
        /// 채점 제출 폴더를 삭제
        /// </summary>
        /// <param name="folderPath">채점 제출 폴더 경로</param>
        private static void DeleteSubmitFolder(in string folderPath) {
            // 채점 제출 폴더를 내부 파일까지 전부 삭제한다.
            if (Directory.Exists(folderPath)) {
                Directory.Delete(folderPath, true);
            }
        }

        /// <summary>
        /// 채점 결과에 맞게 JudgeResult 객체의 데이터를 채워 반환
        /// </summary>
        /// <param name="caseCount">테스트 케이스의 개수</param>
        /// <param name="result">채점 결과가 저장되는</param>
        /// <param name="avgExecutionTime">테스트 케이스 평균 실행 시간</param>
        /// <param name="avgMemoryUsage">테스트 케이스 평균 메모리 사용량</param>
        /// <returns>채점 결과에 맞게 데이터가 채워진 JudgeResult 객체</returns>
        private static JudgeResult GetJudgeResult(in int caseCount, ref JudgeResult result, ref double avgExecutionTime, ref long avgMemoryUsage) {
            // 테스트 케이스를 통과하지 못함
            if (result.Result != JudgeResult.JResult.Accepted) {
                return result;
            }

            // 모든 테스트 케이스를 통과

            // 평균 실행 시간, 메모리 사용량 계산
            avgExecutionTime /= caseCount;
            avgMemoryUsage /= caseCount;

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