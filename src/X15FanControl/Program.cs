using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace X15FanControl
{
    internal static class Program
    {
        private const string EcMutexName = "Global\\X15FanControl-EC-Exclusive";

        [STAThread]
        private static int Main(string[] args)
        {
            // 所有模式（GUI和CLI）必须先获取全局EC互斥锁
            bool createdNew;
            Mutex ecMutex = null;
            bool abandoned = false;

            try
            {
                ecMutex = new Mutex(false, EcMutexName, out createdNew);

                if (!createdNew)
                {
                    try
                    {
                        // 等待现有进程释放（最多3秒）
                        if (!ecMutex.WaitOne(3000, false))
                        {
                            if (args.Length > 0)
                            {
                                Console.Error.WriteLine("错误：另一个 X15FanControl 实例正在运行。");
                                Console.Error.WriteLine("请先关闭已有实例再重试。");
                                return 2;
                            }
                            else
                            {
                                MessageBox.Show("X15 风扇控制已在运行中。", "X15 风扇控制",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                                return 1;
                            }
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        // 上一个进程异常退出，我们取得所有权
                        abandoned = true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (args.Length > 0)
                {
                    Console.Error.WriteLine($"无法创建EC互斥锁: {ex.Message}");
                    return 1;
                }
                else
                {
                    MessageBox.Show($"无法创建EC互斥锁: {ex.Message}", "X15 风扇控制",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }

            // 当 abandoned时记录日志但不阻止启动
            if (abandoned)
            {
                string msg = "警告：检测到上一个进程异常退出（EC互斥锁被遗弃）。当前进程已取得控制权。";
                try
                {
                    File.AppendAllText(
                        Path.Combine(Path.GetTempPath(), "X15FanControl-crash.log"),
                        DateTime.Now.ToString("O") + "  " + msg + Environment.NewLine);
                }
                catch { }

                if (args.Length > 0)
                {
                    Console.WriteLine(msg);
                }
            }

            try
            {
                // 命令行模式：优先解析参数，绝不创建GUI
                if (args.Length > 0)
                {
                    try
                    {
                        using (var verifier = new AutoVerification())
                        {
                            if (args[0] == "--verify-cpu-calibration")
                                return verifier.Run();
                            if (args[0] == "--verify-normal-use-readonly")
                                return verifier.RunNormalUseReadOnly(15);
                            if (args[0] == "--verify-normal-use-active")
                                return verifier.RunNormalUseActive(15);
                            if (args[0] == "--verify-gpu-calibration")
                                return verifier.RunGpuCalibration();
                            if (args[0] == "--verify-gpu-active")
                                return verifier.RunGpuActive(15);
                        }
                        Console.WriteLine($"未知命令: {args[0]}");
                        return 1;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"命令行执行异常: {ex.Message}");
                        return 1;
                    }
                }

                // GUI模式：仅在无参数时启动
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate (object sender, ThreadExceptionEventArgs args2)
                {
                    MessageBox.Show(args2.Exception.ToString(), "未处理的 UI 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs args2)
                {
                    try
                    {
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "X15FanControl-crash.log"),
                            DateTime.Now.ToString("O") + Environment.NewLine + args2.ExceptionObject + Environment.NewLine);
                    }
                    catch
                    {
                    }
                };

                Application.Run(new MainForm());
            }
            finally
            {
                // 释放EC互斥锁
                if (ecMutex != null)
                {
                    try
                    {
                        ecMutex.ReleaseMutex();
                    }
                    catch { }
                    ecMutex.Dispose();
                }
            }

            return 0;
        }
    }
}
