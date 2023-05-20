using System.Runtime.InteropServices;

namespace JudgeServer {
    /// <summary>
    /// WinAPI 함수들을 담고 있는 클래스
    /// </summary>
    public static class NativeFunctions {
        /// <summary>
        /// 애플리케이션 핸들을 가지고 온다.
        /// </summary>
        /// <param name="nStdHandle"></param>
        /// <returns></returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int nStdHandle);

        /// <summary>
        /// 콘솔 모드를 가지고 온다.
        /// </summary>
        /// <param name="hConsoleHandle"></param>
        /// <param name="lpMode"></param>
        /// <returns></returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        /// <summary>
        /// 콘솔모드를 설정한다.
        /// </summary>
        /// <param name="hConsoleHandle"></param>
        /// <param name="dwMode"></param>
        /// <returns></returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }

    /// <summary>
    /// 콘솔창을 설정하는 기능을 담는 클래스
    /// </summary>
    public static class ConsoleWindowSetting {
        public static void DisableQuickEditMode() {
            IntPtr consoleHandle = NativeFunctions.GetStdHandle(-10);
            UInt32 consoleMode;
            NativeFunctions.GetConsoleMode(consoleHandle, out consoleMode);
            consoleMode &= ~((uint)0x0040);
            NativeFunctions.SetConsoleMode(consoleHandle, consoleMode);
        }
    }
}
