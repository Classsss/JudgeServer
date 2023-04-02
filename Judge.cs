namespace JudgeServer {
    public class Judge {
        // 각 언어들의 심볼릭 상수
        // NOTE : 채점 DB에서 과제의 요구 언어를 string으로 저장할 것으로 예상하여 상수의 값을 string으로 설정함
        private const string C = "c";
        private const string CPP = "cpp";
        private const string CSHARP = "csharp";
        private const string JAVA = "java";
        private const string PYTHON = "python";

        // Judge 클래스에서 유일하게 접근할 수 있는 Judge 함수 Handler
        // 이 Dictionary 객체를 언어 문자열로 인덱싱하여 JudgeRequest 객체를 인자로 전달하여 사용
        // Ex) JudgeResult result = JudgeHandler["c"](request); == JudgeC(request)
        public static Dictionary<string, Func<JudgeRequest, JudgeResult>> JudgeHandler;

        // static class의 생성자
        static Judge() {
            // Judge 함수 Handler를 초기화
            JudgeHandler = new Dictionary<string, Func<JudgeRequest, JudgeResult>>() {
                { C, JudgeC }, { CPP, JudgeCpp }, { CSHARP, JudgeCSharp }, { JAVA, JudgeJava }, { PYTHON, JudgePython },};
        }

        // C 코드 채점
        private static JudgeResult JudgeC(JudgeRequest request) {
            return new JudgeResult();
        }

        // C++ 코드 채점
        private static JudgeResult JudgeCpp(JudgeRequest request) {
            return new JudgeResult();
        }

        // C# 코드 채점
        private static JudgeResult JudgeCSharp(JudgeRequest request) {
            return new JudgeResult();
        }

        // Java 코드 채점
        private static JudgeResult JudgeJava(JudgeRequest request) {
            return new JudgeResult();
        }

        // Python 코드 채점
        private static JudgeResult JudgePython(JudgeRequest request) {
            return new JudgeResult();
        }
    }
}