using System;
using System.Collections.Generic;
using System.IO;

namespace SwitchMonitor.Tests
{
    /// <summary>
    /// 简易测试运行器 — 无需 NUnit，手动断言。
    /// 适用 .NET 4.0 / XP 环境，零外部依赖。
    /// </summary>
    public static class TestRunner
    {
        private static int _passed;
        private static int _failed;
        private static string _currentTest;

        public static void RunAll()
        {
            _passed = 0;
            _failed = 0;

            Console.WriteLine("=== D4 Pipeline Integration Tests ===");
            Console.WriteLine();

            D4Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== D5 UI Alarm Display + Diagnosis Config Tests ===");
            Console.WriteLine();

            D5Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== D6 Trend Analysis + Reference Curve Tests ===");
            Console.WriteLine();

            D6Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== D7 Current Baseline Tests ===");
            Console.WriteLine();

            D7Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== D8 Standard Curve Builder Tests ===");

            D8Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== D9 Drift Estimator Tests ===");

            D9Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== N01-1 SQLite Storage Layer Tests ===");
            Console.WriteLine();

            N01_1Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== N01-2 DataForwarder Tests ===");
            Console.WriteLine();

            N01_2Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== N01-3 Network Layer Receiver Tests ===");
            Console.WriteLine();

            N01_3Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== N01-5 Config Model Upgrade Tests ===");
            Console.WriteLine();

            N01_5Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== N01-6 Integration Tests ===");
            Console.WriteLine();

            N01_6Tests.Run();

            Console.WriteLine();
            Console.WriteLine("=== 测试完成: {0} 通过, {1} 失败 ===", _passed, _failed);
        }

        public static void Test(string name, Action test)
        {
            _currentTest = name;
            try
            {
                test();
                _passed++;
                Console.WriteLine("  PASS: " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("  FAIL: " + name);
                Console.WriteLine("         " + ex.Message);
            }
        }

        public static void AssertEqual<T>(T expected, T actual, string label)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception(string.Format("{0}: 期望={1}, 实际={2}", label, expected, actual));
        }

        public static void AssertEqual(double expected, double actual, double tolerance, string label)
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new Exception(string.Format("{0}: 期望={1}, 实际={2} (容差={3})", label, expected, actual, tolerance));
        }

        public static void AssertTrue(bool condition, string label)
        {
            if (!condition)
                throw new Exception(label + ": 期望=true, 实际=false");
        }

        public static void AssertFalse(bool condition, string label)
        {
            if (condition)
                throw new Exception(label + ": 期望=false, 实际=true");
        }

        public static void AssertNotNull(object obj, string label)
        {
            if (obj == null)
                throw new Exception(label + ": 期望非null");
        }

        public static void AssertFileExists(string path, string label)
        {
            if (!File.Exists(path))
                throw new Exception(label + ": 文件不存在: " + path);
        }

        public static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "SwitchMonitor_Tests_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static int ExitCode
        {
            get { return _failed > 0 ? 1 : 0; }
        }
    }
}
