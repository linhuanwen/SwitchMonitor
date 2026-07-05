using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using SwitchMonitor.Common;
using SwitchMonitor.Data;

namespace SwitchMonitor.Tests
{
    public class AlarmThresholdTests
    {
        static int p = 0;
        static int f = 0;

        public static (int passed, int failed) RunAll()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine();
            Console.WriteLine("=== Slice 8-Alarm: alarm threshold config tests ===");
            Console.WriteLine();
            T1(); T2(); T3(); T4(); T5(); T6(); T7(); T8(); T9(); T10();
            Console.WriteLine();
            Console.WriteLine("=== Slice 8-Alarm results ===");
            Console.WriteLine("Passed: {0}, Failed: {1}", p, f);
            return (p, f);
        }

        static void T1() {
            Console.WriteLine("--- Cycle 1a: model creation ---");
            try {
                var c = new PhaseThresholdConfig { Enabled = true, UpperLimit = 2.0f, UpperColor = "#FF0000", UpperLineStyle = "dash" };
                A(c.Enabled, "Enabled"); A(c.UpperLimit == 2.0f, "UpperLimit");
                var cfg = new AlarmThresholdConfig {
                    Current = new PhaseThresholdConfig { Enabled = true, UpperLimit = 2.0f, UpperColor = "#FF0000", UpperLineStyle = "dash" },
                    Power = new PhaseThresholdConfig { Enabled = true, UpperLimit = 1.5f, UpperColor = "#FF0000", UpperLineStyle = "dash" }
                };
                A(cfg.Current != null, "Current not null");
                A(cfg.Power != null, "Power not null");
                Console.WriteLine("  [PASS] Cycle 1a"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void T2() {
            Console.WriteLine("--- Cycle 1b: JSON round trip ---");
            try {
                var cfg = new AlarmThresholdConfig {
                    Current = new PhaseThresholdConfig { Enabled = true, UpperLimit = 3.5f, UpperColor = "#FF4500", UpperLineStyle = "solid" },
                    Power = new PhaseThresholdConfig { Enabled = false, UpperLimit = 2.0f, UpperColor = "#DC143C", UpperLineStyle = "dash" }
                };
                string json = cfg.ToJson();
                A(json.Contains("alarmThresholds"), "JSON has alarmThresholds");
                var deser = AlarmThresholdConfig.FromJson(json);
                A(deser != null, "deserialized not null");
                A(deser.Current.UpperLimit == 3.5f, "round-trip Current.UpperLimit");
                A(deser.Power.Enabled == false, "round-trip Power.Enabled");
                Console.WriteLine("  [PASS] Cycle 1b"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void T3() {
            Console.WriteLine("--- Cycle 2a: defaults ---");
            try {
                string tp = Path.Combine(Path.GetTempPath(), "at1.json");
                if (File.Exists(tp)) File.Delete(tp);
                var mgr = new ConfigManager(tp);
                var cfg = mgr.GetAlarmThresholds();
                A(cfg != null, "default not null");
                A(cfg.Current.UpperLimit == 2.0f, "default Current==2.0");
                A(cfg.Power.UpperLimit == 1.5f, "default Power==1.5");
                if (File.Exists(tp)) File.Delete(tp);
                Console.WriteLine("  [PASS] Cycle 2a"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void T4() {
            Console.WriteLine("--- Cycle 2b: save and reload ---");
            try {
                string tp = Path.Combine(Path.GetTempPath(), "at2.json");
                if (File.Exists(tp)) File.Delete(tp);
                var mgr = new ConfigManager(tp);
                var cfg = mgr.GetAlarmThresholds();
                cfg.Current.UpperLimit = 4.0f; cfg.Power.Enabled = false;
                mgr.SaveAlarmThresholds(cfg);
                A(File.Exists(tp), "file created");
                var mgr2 = new ConfigManager(tp);
                var ld = mgr2.GetAlarmThresholds();
                A(ld.Current.UpperLimit == 4.0f, "reload Current==4.0");
                A(ld.Power.Enabled == false, "reload Power.Enabled==false");
                var mgr3 = new ConfigManager(tp);
                var ar = mgr3.GetAlarmThresholds();
                A(ar.Current.UpperLimit == 4.0f, "restart preserves Current");
                if (File.Exists(tp)) File.Delete(tp);
                Console.WriteLine("  [PASS] Cycle 2b"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void T5() {
            Console.WriteLine("--- Cycle 2c: update ---");
            try {
                string tp = Path.Combine(Path.GetTempPath(), "at3.json");
                if (File.Exists(tp)) File.Delete(tp);
                var mgr = new ConfigManager(tp);
                var cfg = mgr.GetAlarmThresholds();
                cfg.Current.UpperLimit = 5.0f; mgr.SaveAlarmThresholds(cfg);
                cfg.Current.UpperLimit = 7.5f; cfg.Power.UpperLimit = 4.2f; mgr.SaveAlarmThresholds(cfg);
                var mgr2 = new ConfigManager(tp);
                var lt = mgr2.GetAlarmThresholds();
                A(lt.Current.UpperLimit == 7.5f, "latest Current==7.5");
                A(lt.Power.UpperLimit == 4.2f, "latest Power==4.2");
                if (File.Exists(tp)) File.Delete(tp);
                Console.WriteLine("  [PASS] Cycle 2c"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void T6() {
            Console.WriteLine("--- Cycle 3a: valid values ---");
            try {
                A(ThresholdValidator.ValidateUpperLimit("2.0") == null, "2.0 ok");
                A(ThresholdValidator.ValidateUpperLimit("0.0") == null, "0.0 ok");
                A(ThresholdValidator.ValidateUpperLimit("999.9") == null, "999.9 ok");
                Console.WriteLine("  [PASS] Cycle 3a"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void T7() {
            Console.WriteLine("--- Cycle 3b: invalid values ---");
            try {
                A(ThresholdValidator.ValidateUpperLimit("abc") != null, "abc rejected");
                A(ThresholdValidator.ValidateUpperLimit("") != null, "empty rejected");
                A(ThresholdValidator.ValidateUpperLimit("-5.0") != null, "negative rejected");
                A(ThresholdValidator.ValidateUpperLimit("1000.0") != null, ">999.9 rejected");
                Console.WriteLine("  [PASS] Cycle 3b"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void T8() {
            Console.WriteLine("--- Cycle 3c: boundary ---");
            try {
                A(ThresholdValidator.ValidateUpperLimit("0.0") == null, "0.0 ok");
                A(ThresholdValidator.ValidateUpperLimit("999.9") == null, "999.9 ok");
                A(ThresholdValidator.ValidateUpperLimit("1000.0") != null, "1000.0 rejected");
                Console.WriteLine("  [PASS] Cycle 3c"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void T9() {
            Console.WriteLine("--- Cycle 4: enable/disable ---");
            try {
                var cfg = new AlarmThresholdConfig {
                    Current = new PhaseThresholdConfig { Enabled = true, UpperLimit = 2.0f, UpperColor = "#FF0000", UpperLineStyle = "dash" },
                    Power = new PhaseThresholdConfig { Enabled = false, UpperLimit = 1.5f, UpperColor = "#FF0000", UpperLineStyle = "dash" }
                };
                A(cfg.IsCurrentThresholdActive == true, "Current active");
                A(cfg.IsPowerThresholdActive == false, "Power inactive");
                cfg.Current.Enabled = false; cfg.Power.Enabled = true;
                A(cfg.IsCurrentThresholdActive == false, "Current now inactive");
                A(cfg.IsPowerThresholdActive == true, "Power now active");
                Console.WriteLine("  [PASS] Cycle 4"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void T10() {
            Console.WriteLine("--- Cycle 5: cancel ---");
            try {
                string tp = Path.Combine(Path.GetTempPath(), "at_cancel.json");
                if (File.Exists(tp)) File.Delete(tp);
                var mgr = new ConfigManager(tp);
                var orig = mgr.GetAlarmThresholds();
                orig.Current.UpperLimit = 3.0f; orig.Power.UpperLimit = 2.0f;
                mgr.SaveAlarmThresholds(orig);
                orig.Current.UpperLimit = 999.0f;
                var mgr2 = new ConfigManager(tp);
                var rld = mgr2.GetAlarmThresholds();
                A(rld.Current.UpperLimit == 3.0f, "cancel preserves Current");
                A(rld.Power.UpperLimit == 2.0f, "cancel preserves Power");
                if (File.Exists(tp)) File.Delete(tp);
                Console.WriteLine("  [PASS] Cycle 5"); p++;
            } catch (Exception ex) { Console.WriteLine("  [FAIL]: {0}", ex.Message); f++; }
        }

        static void A(bool condition, string message) {
            if (!condition) { Console.WriteLine("    ASSERT FAIL: {0}", message); throw new Exception(message); }
        }
    }
}