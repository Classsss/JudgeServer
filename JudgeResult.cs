namespace JudgeServer {
    public class JudgeResult {
        // 문제가 모든 테스트 케이스를 통과해 맞을 때만 true
        public bool IsCorrect { get; set; }
        // 평균 실행 시간(ms)
        public double ExecutionTime { get; set; } = 0;
        // 평균 메모리 사용량(?) // TODO : 단위 지정 필요
        public long MemoryUsage { get; set; } = 0;
        // 메시지가 설정될 때만 not null
        public string? CompileErrorMsg { get; set; } = null;
        // 메시지가 설정될 때만 not null
        public string? RuntimeErrorMsg { get; set; } = null;
        // 시간 초과가 발생했을 때만 true
        public bool IsTimeOut { get; set; } = false;
        // 메모리 초과가 발생했을 때만 true
        public bool IsExceedMemory { get; set; } = false;
    }
}
