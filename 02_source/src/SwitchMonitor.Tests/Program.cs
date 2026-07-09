using System;

namespace SwitchMonitor.Tests
{
    class Program
    {
        static int Main(string[] args)
        {
            TestRunner.RunAll();
            return TestRunner.ExitCode;
        }
    }
}
