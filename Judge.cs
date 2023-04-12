using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Reflection;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace JudgeServer {
    public class Judge {
        // 각 언어들의 심볼릭 상수
        // NOTE : 채점 DB에서 과제의 요구 언어를 string으로 저장할 것으로 예상하여 상수의 값을 string으로 설정함
        private const string C = "c";
        private const string CPP = "cpp";
        private const string CSHARP = "csharp";
        private const string JAVA = "java";
        private const string PYTHON = "python";

        // 채점 폴더가 생성될 기본 경로
        private const string SUBMIT_FOLDER_PATH = @"C:\Users\LeeHaYoon\Desktop\docker\";

        // 도커 이미지 이름
        private const string IMAGE_NAME = "leehayoon/judge";

        // Judge 클래스에서 유일하게 접근할 수 있는 Judge 함수 Handler
        // 이 Dictionary 객체를 언어 문자열로 인덱싱하여 JudgeRequest 객체를 인자로 전달하여 사용
        // Ex) JudgeResult result = JudgeHandler["c"](request); == JudgeC(request)
        public static Dictionary<string, Func<JudgeRequest, Task<JudgeResult>>> JudgeHandler;

        // static class의 생성자
        static Judge() {
            // Judge 함수 Handler를 초기화
            JudgeHandler = new Dictionary<string, Func<JudgeRequest, Task<JudgeResult>>>() {
                { C, JudgeCAsync }, { CPP, JudgeCppAsync }, { CSHARP, JudgeCSharpAsync }, { JAVA, JudgeJavaAsync }, { PYTHON, JudgePythonAsync } };
        }

        /// <summary>
        /// 채점 요청을 받은 코드를 채점함.
        /// </summary>
        /// <param name="request">ClassHub에서 요청한 정보가 담긴 객체</param>
        /// <returns>채점 결과 정보가 담긴 객체</returns>
        public static async Task<JudgeResult> JudgeCodeAsync(JudgeRequest request) {
            // 반환할 채점 정보를 저장하는 객체
            JudgeResult result = new JudgeResult();
            // 전달받은 코드
            string? code = request.Code;
            // 코드의 언어
            // TODO : JudgeRequest에서 Language 속성 제거?
            string? language = request.Language;

            // 입력 테스트 케이스
            List<string> inputCases;
            // 출력 테스트 케이스
            List<string> outputCases;
            // 실행 시간(ms) 제한
            double executionTimeLimit;
            // 메모리 사용량(KB) 제한
            long memoryUsageLimit;

            // 채점 DB에서 입출력 케이스, 실행 시간 제한, 메모리 사용량 제한을 받아옴
            GetTestCases(out inputCases, out outputCases, out executionTimeLimit, out memoryUsageLimit);

            // 채점 요청별로 사용할 유니크한 폴더명
            string folderName;

            // 유저 제출 폴더, 입력케이스, 컴파일 에러 메시지, 런타임 에러 메시지, 실행 결과, 실행 시간과 메모리 사용량이 저장되는 경로들
            string folderPath, inputFilePath, compileErrorFilePath, runtimeErrorFilePath, resultFilePath, statFilePath;

            // 채점 제출 폴더를 생성하고 내부에 생성되는 파일들의 경로를 받아옴
            CreateSubmitFolder(in language, out folderName, out folderPath, out inputFilePath, out compileErrorFilePath, out runtimeErrorFilePath, out resultFilePath, out statFilePath);

            // 코드를 언어에 맞는 형식을 가지는 파일로 저장
            string codeFilePath;
            CreateCodeFile(in folderPath, in code, in language, out codeFilePath);

            // Docker Hub에서의 이미지 태그
            string imageTag = language;

            // Docker client 초기화
            // TODO : dockerClient, volumeMapping이 null이 아닐 때 예외처리 필요
            var dockerTuple = await InitDockerClientAsync(imageTag, folderPath, folderName);
            DockerClient? dockerClient = dockerTuple.Item1;
            Dictionary<string, string>? volumeMapping = dockerTuple.Item2;

            // 테스트 케이스들의 평균 실행 시간과 메모리 사용량
            double avgExecutionTime = 0;
            long avgMemoryUsage = 0;

            // 케이스 횟수
            int caseCount = outputCases.Count();

            // 테스트 케이스 수행
            for (int i = 0; i < caseCount; i++) {
                // 입력 케이스를 파일로 저장
                File.WriteAllText(inputFilePath, inputCases[i]);

                // 컨테이너 구동
                await RunDockerContainerAsync(dockerClient, volumeMapping, imageTag, folderName);

                // 컴파일 에러인지 체크
                if (IsOccuredCompileError(in compileErrorFilePath, ref result)) {
                    break;
                }

                // 런타임 에러가 발생했는지 체크
                if (IsOccuredRuntimeError(in runtimeErrorFilePath, ref result)) {
                    break;
                }

                // 실행 시간과 메모리 사용량
                double executionTime;
                long memoryUsage;

                // 실행 시간, 메모리 사용량 측정 값을 받아옴
                GetStats(in statFilePath, out executionTime, out memoryUsage);
                Console.WriteLine($"limit : {executionTimeLimit} / acutal : {executionTime}");

                // 시간 초과가 발생했는지 체크
                if (IsExceededTimeLimit(in executionTime, in executionTimeLimit, ref result)) {
                    break;
                }

                // 메모리 초과가 발생했는지 체크
                if (IsExceededMemoryLimit(in memoryUsage, in memoryUsageLimit, ref result)) {
                    break;
                }

                // 평균 실행 시간 및 메모리 사용량 계산
                avgExecutionTime += executionTime;
                avgMemoryUsage += memoryUsage;

                // 현재 진행 중인 테스트 케이스의 출력 케이스
                string outputCase = outputCases[i];

                // 실행 결과와 출력 케이스 비교
                if (!JudgeTestCase(in outputCase, in resultFilePath, ref result)) {
                    break;
                }

                Console.WriteLine($"{i + 1}번째 케이스 통과");

                // 테스트 케이스에서 사용하는 파일 초기화
                InitFile(in inputFilePath, in resultFilePath);
            }

            // 채점 제출 폴더 삭제
            // TODO : 현재는 디버깅을 위해 주석처리
            //DeleteSubmitFolder(in folderPath);

            // 모든 테스트 케이스를 수행하면 결과를 저장해 JudgeResult 객체 반환
            return GetJudgeResult(in caseCount, ref result, ref avgExecutionTime, ref avgMemoryUsage);
        }

        /// <summary>
        /// 채점 DB에서 입출력 테스트 케이스, 실행 시간 제한, 메모리 사용량 제한 값을 받아옴
        /// </summary>
        /// <param name="inputCases">입력 테스트 케이스</param>
        /// <param name="outputCases">출력 테스트 케이스</param>
        /// <param name="executionTimeLimit">실행 시간 제한</param>
        /// <param name="memoryUsageLimit">메모리 사용량 제한</param>
        private static void GetTestCases(out List<string> inputCases, out List<string> outputCases, out double executionTimeLimit, out long memoryUsageLimit) {
            // TODO : 채점 DB에서 입출력 테스트 케이스, 실행 시간 제한, 메모리 사용량 제한 받아오기
            // TODO : 채점 DB에서 가져오는 값들이 교수가 과제를 등록할 때 정할 수 있다면, 정해진 값들만 사용하도록 코드 개선 필요
            // 입력 테스트 케이스
            inputCases = new List<string> { "1 2", "5 9", "-3 3" };
            // 출력 테스트 케이스
            outputCases = new List<string> { "3", "14", "0" };
            // 실행 시간(ms) 제한 - 500ms
            executionTimeLimit = 500;
            // 메모리 사용량(KB) 제한 - 512KB
            memoryUsageLimit = 512;
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

            // 폴더가 존재하지 않는 경우에만 폴더를 생성합니다.
            if (!Directory.Exists(folderPath)) {
                Directory.CreateDirectory(folderPath);
                Console.WriteLine($"폴더가 생성되었습니다: {folderPath}");
            }

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
        private static void CreateCodeFile(in string folderPath, in string code, in string language, out string codeFilePath) {
            // 파이썬의 경우 언어 이름과 파일 형식이 다름
            if (language == "python") {
                codeFilePath = Path.Combine(folderPath, "Main.py");
            } else {
                codeFilePath = Path.Combine(folderPath, $"Main.{language}");
            }
            File.WriteAllText(codeFilePath, code);
        }

        /// <summary>
        /// DockerClient를 생성하고 이미지를 빌드하여 컨테이너 생성을 준비하는 비동기 메소드
        /// </summary>
        /// <param name="imageTag">도커 이미지 태그</param>
        /// <param name="folderPath">채점 제출 폴더 경로</param>
        /// <param name="folderName">채점 제출 폴더 명</param>
        /// <returns>생성된 DockerClient와 volumeMapping을 ValueTuple로 반환</returns>
        private static async Task<ValueTuple<DockerClient?, Dictionary<string, string>?>> InitDockerClientAsync(string imageTag, string folderPath, string folderName) {
            // Docker client 생성
            DockerClient? dockerClient = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();

            // 이미지 다운로드
            await dockerClient.Images.CreateImageAsync(new ImagesCreateParameters {
                FromImage = IMAGE_NAME, Tag = imageTag
            }, new AuthConfig(), new Progress<JSONMessage>());

            // 볼륨 맵핑 - 로컬 유저 폴더 : 컨테이너 내부 유저 폴더
            Dictionary<string, string>? volumeMapping = new Dictionary<string, string> { { folderPath, $"/app/{folderName}" } };

            // ValueTuple로 반환
            return (dockerClient, volumeMapping);
        }

        /// <summary>
        /// 빌드한 이미지로 컨테이너 생성, 실행, 제거를 수행하는 비동기 메소드
        /// </summary>
        /// <param name="dockerClient">도커 클라이언트</param>
        /// <param name="volumeMapping">컨테이너와의 볼륨 매핑</param>
        /// <param name="imageTag">도커 이미지 태그</param>
        /// <param name="folderName">채점 제출 폴더명</param>
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
        /// 컴파일 에러가 발생했는지 체크
        /// </summary>
        /// <param name="compileErrorFilePath">컴파일 에러 메시지가 저장되는 경로</param>
        /// <param name="result">채점 결과를 저장하는 객체</param>
        /// <returns>컴파일 에러가 발생할 때 true</returns>
        private static bool IsOccuredCompileError(in string compileErrorFilePath, ref JudgeResult result) {
            // 컴파일 에러 발생
            if (File.Exists(compileErrorFilePath)) {
                string errorMsg = File.ReadAllText(compileErrorFilePath);

                if (errorMsg.Length != 0) {
                    Console.WriteLine("Compile Error Occured : ", errorMsg);

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
        private static bool IsOccuredRuntimeError(in string runtimeErrorFilePath, ref JudgeResult result) {
            // 런타임 에러가 발생했는지 체크
            if (File.Exists(runtimeErrorFilePath)) {
                string errorMsg = File.ReadAllText(runtimeErrorFilePath);

                if (errorMsg.Length != 0) {
                    Console.WriteLine("Runtime Error Occured : ", errorMsg);

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
            string actualOutput = File.Exists(resultFilePath) ? File.ReadAllText(resultFilePath) : "";
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

        // C 코드 채점
        private static async Task<JudgeResult> JudgeCAsync(JudgeRequest request) {
            // 반환할 채점 정보를 저장하는 객체
            JudgeResult result = new JudgeResult();
            // c언어 코드
            string? code = request.Code;
            // 코드의 언어
            // TODO : JudgeRequest에서 Language 속성 제거?
            string? language = request.Language;

            // 입력 테스트 케이스
            List<string> inputCases;
            // 출력 테스트 케이스
            List<string> outputCases;
            // 실행 시간(ms) 제한
            double executionTimeLimit;
            // 메모리 사용량(KB) 제한
            long memoryUsageLimit;

            // 채점 DB에서 입출력 케이스, 실행 시간 제한, 메모리 사용량 제한을 받아옴
            GetTestCases(out inputCases, out outputCases, out executionTimeLimit, out memoryUsageLimit);

            // 채점 요청별로 사용할 유니크한 폴더명
            string folderName;

            // 유저 제출 폴더, 입력케이스, 컴파일 에러 메시지, 런타임 에러 메시지, 실행 결과, 실행 시간과 메모리 사용량이 저장되는 경로들
            string folderPath, inputFilePath, compileErrorFilePath, runtimeErrorFilePath, resultFilePath, statFilePath;

            // 채점 제출 폴더를 생성하고 내부에 생성되는 파일들의 경로를 받아옴
            CreateSubmitFolder(in language, out folderName, out folderPath, out inputFilePath, out compileErrorFilePath, out runtimeErrorFilePath, out resultFilePath, out statFilePath);

            // 코드를 언어에 맞는 형식을 가지는 파일로 저장
            string codeFilePath;
            CreateCodeFile(in folderPath, in code, in language, out codeFilePath);

            // Docker Hub에서의 이미지 태그
            string imageTag = language;

            // Docker client 초기화
            // TODO : dockerClient, volumeMapping이 null이 아닐 때 예외처리 필요
            var dockerTuple = await InitDockerClientAsync(imageTag, folderPath, folderName);
            DockerClient? dockerClient = dockerTuple.Item1;
            Dictionary<string, string>? volumeMapping = dockerTuple.Item2;

            // 테스트 케이스들의 평균 실행 시간과 메모리 사용량
            double avgExecutionTime = 0;
            long avgMemoryUsage = 0;

            // 케이스 횟수
            int caseCount = outputCases.Count();

            // 테스트 케이스 수행
            for (int i = 0; i < caseCount; i++) {
                // 입력 케이스를 파일로 저장
                File.WriteAllText(inputFilePath, inputCases[i]);

                // 컨테이너 구동
                await RunDockerContainerAsync(dockerClient, volumeMapping, imageTag, folderName);

                // 컴파일 에러인지 체크
                if (IsOccuredCompileError(in compileErrorFilePath, ref result)) {
                    break;
                }

                // 런타임 에러가 발생했는지 체크
                if (IsOccuredRuntimeError(in runtimeErrorFilePath, ref result)) {
                    break;
                }

                // 실행 시간과 메모리 사용량
                double executionTime;
                long memoryUsage;

                // 실행 시간, 메모리 사용량 측정 값을 받아옴
                GetStats(in statFilePath, out executionTime, out memoryUsage);
                Console.WriteLine($"limit : {executionTimeLimit} / acutal : {executionTime}");

                // 시간 초과가 발생했는지 체크
                if (IsExceededTimeLimit(in executionTime, in executionTimeLimit, ref result)) {
                    break;
                }

                // 메모리 초과가 발생했는지 체크
                if (IsExceededMemoryLimit(in memoryUsage, in memoryUsageLimit, ref result)) {
                    break;
                }

                // 평균 실행 시간 및 메모리 사용량 계산
                avgExecutionTime += executionTime;
                avgMemoryUsage += memoryUsage;

                // 현재 진행 중인 테스트 케이스의 출력 케이스
                string outputCase = outputCases[i];

                // 실행 결과와 출력 케이스 비교
                if (!JudgeTestCase(in outputCase, in resultFilePath, ref result)) {
                    break;
                }

                Console.WriteLine($"{i + 1}번째 케이스 통과");

                // 테스트 케이스에서 사용하는 파일 초기화
                InitFile(in inputFilePath, in resultFilePath);
            }

            // 채점 제출 폴더 삭제
            // TODO : 현재는 디버깅을 위해 주석처리
            //DeleteSubmitFolder(in folderPath);

            // 모든 테스트 케이스를 수행하면 결과를 저장해 JudgeResult 객체 반환
            return GetJudgeResult(in caseCount, ref result, ref avgExecutionTime, ref avgMemoryUsage);
        }

        // C++ 코드 채점
        private static async Task<JudgeResult> JudgeCppAsync(JudgeRequest request) {
            return new JudgeResult();
        }

        // C# 코드 채점
        private static async Task<JudgeResult> JudgeCSharpAsync(JudgeRequest request) {
            // 반환할 객체
            JudgeResult result = new JudgeResult();

            // TODO : 채점 DB에서 입출력 테스트 케이스, 실행 시간 제한, 메모리 사용량 제한 받아오기
            // TODO : 채점 DB에서 가져오는 값들이 교수가 과제를 등록할 때 정할 수 있다면, 정해진 값들만 사용하도록 코드 개선 필요
            // 입력 테스트 케이스
            List<string> inputCases = new List<string>() { "1 2", "5 9", "-3 3" };
            // 출력 테스트 케이스
            List<string> outputCases = new List<string>() { "3", "14", "0" };
            // 실행 시간(ms) 제한 - 500ms
            double executionTimeLimit = 500;
            // 메모리 사용량(B) 제한 - 512B
            long memoryUsageLimit = 512;

            // 컴파일 및 실행을 위한 준비
            var syntaxTree = CSharpSyntaxTree.ParseText(request.Code);
            string assemblyName = Path.GetRandomFileName();
            var references = new MetadataReference[]
            {
                MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).GetTypeInfo().Assembly.Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime, Version=7.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a").Location),
            };

            var compilation = CSharpCompilation.Create(
                assemblyName,
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.ConsoleApplication));

            // 컴파일
            using (var ms = new MemoryStream()) {
                var emitResult = compilation.Emit(ms);

                // 컴파일 실패
                if (!emitResult.Success) {
                    Console.WriteLine("컴파일 오류 발생");

                    // 런타임 에러 메시지 저장
                    IEnumerable<Diagnostic> failures = emitResult.Diagnostics.Where(diagnostic =>
                        diagnostic.IsWarningAsError ||
                        diagnostic.Severity == DiagnosticSeverity.Error);

                    string errors = "";
                    foreach (Diagnostic diagnostic in failures) {
                        var lineSpan = diagnostic.Location.GetLineSpan();
                        int lineNumber = lineSpan.StartLinePosition.Line + 1; // 0-based index to 1-based index

                        errors += $"{diagnostic.Id}: {diagnostic.GetMessage()} at line {lineNumber}\n";
                        Console.Error.WriteLine($"{diagnostic.Id}: {diagnostic.GetMessage()} at line {lineNumber}");
                    }

                    result.Result = JudgeResult.JResult.CompileError;
                    result.Message = errors;
                    return result;
                } 
                // 컴파일 성공
                else {
                    ms.Seek(0, SeekOrigin.Begin);
                    var assembly = Assembly.Load(ms.ToArray());

                    // 프로그램 실행 준비
                    var type = assembly.GetType("Test");
                    var mainMethod = type.GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    // 테스트 케이스들의 평균 실행 시간과 메모리 사용량
                    double avgExecutionTime = 0;
                    long avgMemoryUsage = 0;

                    // 테스트 케이스 만큼 실행
                    for (int i = 0; i < inputCases.Count(); i++) {
                        Console.WriteLine($"{i} 번째 테스트 케이스 시작");
                        
                        // inputCase를 표준 입력으로 설정
                        string input = inputCases[i];
                        TextReader originalIn = Console.In;
                        Console.SetIn(new StringReader(input));

                        string output;                  // 실행 결과
                        double executionTime = 0;       // 실행 시간
                        long memoryUsage = 0;           // 메모리 사용량

                        // 실행 결과를 표준 출력으로 설정
                        TextWriter originalOut = Console.Out;
                        using (var sw = new StringWriter()) {
                            Console.SetOut(sw);

                            // 스톱워치 시작
                            Stopwatch watch = new Stopwatch();
                            watch.Start();

                            // 실행 전의 메모리 사용량 측정
                            Process currentProcess = Process.GetCurrentProcess();
                            long memoryUsageBefore = currentProcess.WorkingSet64;

                            try {
                                // 실행
                                mainMethod.Invoke(null, new object[] { });

                                // 실행 결과를 변수에 저장
                                output = sw.ToString();
                            } 
                            // 런타임 에러가 발생해 프로그램 실행 중지
                            catch (TargetInvocationException ex) {
                                Exception innerException = ex.InnerException; // 실제 발생한 런타임 에러 메시지
                                Console.Error.WriteLine($"Runtime Error: {innerException.Message}");

                                result.Result = JudgeResult.JResult.RuntimeError;
                                result.Message = "Runtime Error: " + innerException.Message;
                                break;
                            }
                            finally {
                                // 표준 입력 및 출력 복원
                                Console.SetIn(originalIn);
                                Console.SetOut(originalOut);
                            }

                            // 스톱워치를 멈춰 걸린 시간으로 실행 시간 측정
                            watch.Stop();
                            TimeSpan elapsedTime = watch.Elapsed;
                            executionTime = watch.ElapsedMilliseconds;
                            avgExecutionTime += executionTime;

                            // 프로세스 실행 후의 메모리 사용량 측정
                            long memoryUsageAfter = currentProcess.WorkingSet64;

                            // 메모리 사용량 계산
                            memoryUsage = memoryUsageAfter - memoryUsageBefore;
                            avgMemoryUsage += memoryUsage;
                        }

                        Console.WriteLine($"Execution Time: {executionTime} ms Memory Used: {memoryUsage} byte");

                        // TODO : 추가적으로 채점 수행 필요
                        // 시간 초과
                        if (executionTime > executionTimeLimit) {
                            Console.WriteLine("시간 초과");

                            result.Result = JudgeResult.JResult.TimeLimitExceeded;
                            break;
                        }

                        // 메모리 사용량 초과
                        if (memoryUsage > memoryUsageLimit) {
                            Console.WriteLine("메모리 사용량 초과");

                            result.Result = JudgeResult.JResult.MemoryLimitExceeded;
                            break;
                        }

                        Console.WriteLine($"output : {output} outputCase : {outputCases[i]}");

                        // 테스트 케이스 통과
                        if (output == outputCases[i]) {
                            Console.WriteLine("맞았습니다.");

                            result.Result = JudgeResult.JResult.Accepted;
                        } 
                        // 틀림
                        else {
                            Console.WriteLine("틀렸습니다.");
                            
                            result.Result = JudgeResult.JResult.WrongAnswer;
                            break;
                        }
                    }

                    // 모든 테스트 케이스를 통과하지 못해 break로 빠져나오면 바로 반환
                    if (result.Result != JudgeResult.JResult.Accepted) {
                        return result;
                    }
                    // 모든 테스트 케이스를 통과
                    else {
                        avgExecutionTime /= inputCases.Count();
                        avgMemoryUsage /= inputCases.Count();

                        Console.WriteLine("avgExecutionTime : " + avgExecutionTime);
                        Console.WriteLine("avgMemoryUsage : " + avgMemoryUsage);

                        result.ExecutionTime = avgExecutionTime;
                        result.MemoryUsage = avgMemoryUsage;

                        return result;
                    }
                }
            }
        }

        // Java 코드 채점
        private static async Task<JudgeResult> JudgeJavaAsync(JudgeRequest request) {
            return new JudgeResult();
        }

        // Python 코드 채점
        private static async Task<JudgeResult> JudgePythonAsync(JudgeRequest request) {
            return new JudgeResult();
        }
    }
}