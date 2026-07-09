using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Ie8Test
{
    class Program
    {
        [STAThread]
        static void Main()
        {
            // 设置 IE8 模拟模式（与 SwitchMonitor 主程序逻辑一致）
            string appName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
            string regPath = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(regPath))
                {
                    if (key != null)
                    {
                        key.SetValue(appName, 8888, RegistryValueKind.DWord); // IE8 Standards Mode
                        Console.WriteLine("IE8 模拟注册表已设置: " + appName + " = 8888");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("注册表写入失败: " + ex.Message);
            }

            // 找到测试页面
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string testPath = Path.Combine(exeDir, "test_diag_ui.html");
            if (!File.Exists(testPath))
            {
                // 回退：尝试相对于源码目录
                testPath = @"d:\Vibe coding\04 DCjiance\SwitchMonitor\02_source\src\SwitchMonitor.UI\Html\test_diag_ui.html";
            }
            if (!File.Exists(testPath))
            {
                MessageBox.Show("找不到测试页面: " + testPath);
                return;
            }

            Console.WriteLine("加载: " + testPath);
            Console.WriteLine("WebBrowser 控件 IE 版本: " + GetWebBrowserVersion());

            // 创建窗口
            var form = new Form
            {
                Text = "IE8 WebBrowser 诊断UI测试 — 模拟XP工控机环境",
                Width = 1200,
                Height = 800,
                StartPosition = FormStartPosition.CenterScreen
            };

            var browser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = false  // 不抑制，让 JS 错误弹窗
            };

            // 捕获 JS 错误
            browser.DocumentCompleted += (s, e) =>
            {
                Console.WriteLine("页面加载完成: " + browser.Url);
                // 检查是否有错误弹窗被捕获
            };

            browser.Navigate(testPath);
            form.Controls.Add(browser);
            Application.Run(form);
        }

        static int GetWebBrowserVersion()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"Software\Microsoft\Internet Explorer"))
                {
                    if (key != null)
                    {
                        var svcVersion = key.GetValue("svcVersion");
                        var version = key.GetValue("Version");
                        if (svcVersion != null) return Convert.ToInt32(svcVersion.ToString().Split('.')[0]);
                        if (version != null) return Convert.ToInt32(version.ToString().Split('.')[0]);
                    }
                }
            }
            catch { }
            return -1;
        }
    }
}
