// 처음 설치 (관리자 권한): 관리센터를 Program Files 에 두고, 목록의 드라이버 · 프로그램 · 코어닷프린터를 깔고,
// 백그라운드 설치 작업 · 자동 확인 작업 · 바로 가기 · 제어판 항목을 등록한다. 제거(--uninstall)도 여기.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CoreDot
{
    class SetupStep
    {
        public string Title;
        public Action<SetupStep> Run;
        public bool Ok;
        public string Result = "";
        public Action<string> Progress;   // 창이 붙인다
    }

    static class SetupMode
    {
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CoreDotManager";
        const string CheckTask = @"\CoreDot\Check";
        public static bool NeedReboot;

        public static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static string SelfPath()
        {
            return Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        }

        /// <summary>관리자 권한으로 자기 자신을 다시 띄운다.</summary>
        public static int Elevate(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(SelfPath(), args);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return 0;
            }
            catch (System.ComponentModel.Win32Exception) { return 1; }   // 관리자 확인에서 '아니요'
        }

        // ================================================================ 설치
        public static int Run(bool silent)
        {
            Core.LogFile = Path.Combine(Paths.DataDir, @"logs\install.log");
            Core.Log("==== 코어닷 관리센터 " + Program.Version + " 설치 시작 (" + SelfPath() + ")");

            CatalogData cat = null;
            Form splash = null;
            if (!silent)
            {
                splash = Splash("프로그램 목록을 받는 중…");
                splash.Show();
                Application.DoEvents();
            }
            try { cat = Catalog.Fetch(); }
            catch (Exception e)
            {
                if (splash != null) splash.Close();
                Core.Log("목록 실패: " + e.Message);
                if (!silent) MessageBox.Show(e.Message, "코어닷 관리센터 설치", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            if (splash != null) splash.Close();

            var steps = BuildSteps(cat);
            if (silent)
            {
                bool all = RunSteps(steps, null);
                LaunchAfter(true);
                return !all ? 1 : NeedReboot ? 3010 : 0;
            }
            Application.Run(new SetupForm(steps));
            return 0;
        }

        static List<SetupStep> BuildSteps(CatalogData cat)
        {
            var steps = new List<SetupStep>();
            steps.Add(new SetupStep { Title = "코어닷 관리센터", Run = delegate(SetupStep s) { InstallManagerFiles(); s.Result = Program.Version + " 설치"; } });
            var engine = new Engine();
            foreach (var it in cat.Items)
            {
                var item = it;
                steps.Add(new SetupStep
                {
                    Title = item.Name + (item.Kind == "driver" ? " 드라이버" : ""),
                    Run = delegate(SetupStep s)
                    {
                        string have = Catalog.InstalledVersion(item);
                        if (have != null && (have == "설치됨" || Core.CompareVersions(have, item.Version) >= 0))
                        {
                            s.Result = "이미 설치됨 (" + have + ")";
                            return;
                        }
                        engine.Report = delegate(string id, string st, string msg) { if (s.Progress != null) s.Progress(msg); };
                        engine.InstallItem(item);
                        if (engine.NeedReboot) NeedReboot = true;
                        s.Result = item.Version + " 설치";
                    }
                });
            }
            steps.Add(new SetupStep { Title = "코어닷프린터", Run = InstallPrinterApp });
            steps.Add(new SetupStep { Title = "자동 확인 · 바로 가기 · 제어판 등록", Run = delegate(SetupStep s) { RegisterAll(); } });
            return steps;
        }

        public static bool RunSteps(List<SetupStep> steps, Action<int, string> report)
        {
            bool all = true;
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                int idx = i;
                s.Progress = delegate(string m) { if (report != null) report(idx, "… " + m); };
                if (report != null) report(i, "… 설치 중");
                Core.Log("---- " + s.Title);
                try
                {
                    s.Run(s);
                    s.Ok = true;
                    if (s.Result == "") s.Result = "완료";
                    if (report != null) report(i, "✔ " + s.Result);
                }
                catch (Exception e)
                {
                    all = false;
                    s.Ok = false;
                    s.Result = e.Message;
                    Core.Log("실패: " + e);
                    if (report != null) report(i, "✘ " + e.Message);
                }
            }
            Core.Log(all ? "==== 모두 완료" : "==== 일부 실패");
            return all;
        }

        public static void LaunchAfter(bool includePrinter)
        {
            try
            {
                if (includePrinter && File.Exists(Path.Combine(Core.AppDir, Core.ExeName)))
                {
                    Core.StopApp(10000);   // 다른 자리에서 켜 둔 예전 판이 있으면 새 자리 것이 안 뜬다 (한 PC 한 개)
                    Core.StartApp(true);
                }
                if (File.Exists(Paths.ManagerExe)) Process.Start("explorer.exe", "\"" + Paths.ManagerExe + "\"");
            }
            catch (Exception e) { Core.Log("실행 실패: " + e.Message); }
        }

        // ---------------------------------------------------------------- 관리센터 파일 · 폴더 권한
        static void InstallManagerFiles()
        {
            Engine.ReplaceManager(SelfPath());
            Directory.CreateDirectory(Paths.Cache);
            Directory.CreateDirectory(Paths.Requests);
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.ServiceLog));
            // ProgramData\CoreDot: SYSTEM · 관리자만 쓰기, 사용자는 읽기 — 받은 설치 파일을 사용자가 바꿔치기 못 하게
            string icacls = Engine.Sys32("icacls.exe");
            Engine.Run(icacls, "\"" + Paths.DataDir + "\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /T /C /Q", 120000);
            // requests: 사용자가 '설치해 줘' 를 적는 곳 (서비스는 여기서 id 만 읽는다)
            Engine.Run(icacls, "\"" + Paths.Requests + "\" /grant *S-1-5-32-545:(OI)(CI)M /C /Q", 60000);
            Core.Log("관리센터 설치: " + Paths.ManagerExe);
        }

        // ---------------------------------------------------------------- 작업 · 바로 가기 · 제어판
        static void RegisterAll()
        {
            RegisterInstallerTask();
            RegisterCheckTask();
            RemoveOldUpdater();
            Shortcuts();
            WriteUninstallEntry(Program.Version);
        }

        static void RegisterInstallerTask()
        {
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>코어닷 관리센터 — 드라이버 · 프로그램 설치 (관리센터가 부를 때만 실행)</Description></RegistrationInfo>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>\"" + Paths.ManagerExe + "\"</Command><Arguments>--service</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            svc.Connect();
            dynamic root = svc.GetFolder("\\");
            dynamic def = svc.NewTask(0);
            def.XmlText = xml;
            // 보안 설명자: SYSTEM · 관리자 = 모든 권한, 로그인한 사용자 = 읽기 + 실행(작업 시작) — 관리자 확인 없이 부를 수 있게
            root.RegisterTaskDefinition(ServiceClient.TaskPath, def, 6, "SYSTEM", Type.Missing, 5, "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)");
            Core.Log("작업 등록: " + ServiceClient.TaskPath + " (SYSTEM)");
        }

        static void RegisterCheckTask()
        {
            string start = DateTime.Now.AddMinutes(10).ToString("yyyy-MM-dd'T'HH:mm:ss");
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>코어닷 관리센터 — 새 버전 · 새 프로그램 확인 (있으면 알림 창)</Description></RegistrationInfo>\r\n" +
"  <Triggers>\r\n" +
"    <LogonTrigger><Enabled>true</Enabled><Delay>PT2M</Delay></LogonTrigger>\r\n" +
"    <TimeTrigger><Repetition><Interval>PT3H</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>\r\n" +
"      <StartBoundary>" + start + "</StartBoundary><Enabled>true</Enabled></TimeTrigger>\r\n" +
"  </Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>\r\n" +
"    <ExecutionTimeLimit>PT12H</ExecutionTimeLimit>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>\"" + Paths.ManagerExe + "\"</Command><Arguments>--check</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            string tmp = Path.Combine(Path.GetTempPath(), "CoreDotCheckTask.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = Engine.Run(Engine.Sys32("schtasks.exe"), "/Create /TN \"" + CheckTask + "\" /XML \"" + tmp + "\" /F", 60000);
            Core.TryDelete(tmp);
            if (code != 0) throw new Exception("자동 확인 작업 등록 실패 (코드 " + code + ")");
        }

        /// <summary>예전 설치기(코어닷프린터 전용 업데이터)가 남긴 것 — 관리센터가 대신한다.</summary>
        static void RemoveOldUpdater()
        {
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                try { dynamic f = svc.GetFolder("\\CoreDotPrinter"); try { f.DeleteTask("Updater", 0); } catch { } root.DeleteFolder("CoreDotPrinter", 0); Core.Log("예전 업데이트 작업 지움"); } catch { }
            }
            catch { }
            foreach (var p in Process.GetProcessesByName("CoreDotUpdater")) { try { p.Kill(); } catch { } }
            Core.TryDelete(Path.Combine(Core.AppDir, "CoreDotUpdater.exe"));
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "코어닷프린터"), true); } catch { }
        }

        static void Shortcuts()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "코어닷");
            Directory.CreateDirectory(menu);
            string app = Path.Combine(Core.AppDir, Core.ExeName);
            MakeLink(Path.Combine(desktop, "코어닷 관리센터.lnk"), Paths.ManagerExe, "", Paths.ProgramDir);
            MakeLink(Path.Combine(menu, "코어닷 관리센터.lnk"), Paths.ManagerExe, "", Paths.ProgramDir);
            if (File.Exists(app))
            {
                MakeLink(Path.Combine(desktop, "코어닷프린터.lnk"), app, "", Core.AppDir);
                MakeLink(Path.Combine(menu, "코어닷프린터.lnk"), app, "", Core.AppDir);
            }
        }

        static void MakeLink(string lnk, string target, string args, string workDir)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.Arguments = args;
            sc.WorkingDirectory = workDir;
            sc.Save();
        }

        public static void WriteUninstallEntry(string version)
        {
            using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = hk.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "코어닷 관리센터");
                k.SetValue("DisplayVersion", version);
                k.SetValue("Publisher", "CoreDotToday");
                k.SetValue("DisplayIcon", Paths.ManagerExe);
                k.SetValue("InstallLocation", Paths.ProgramDir);
                k.SetValue("UninstallString", "\"" + Paths.ManagerExe + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        // ---------------------------------------------------------------- 코어닷프린터 (+ 예전 자리에서 옮겨 오기)
        static void InstallPrinterApp(SetupStep s)
        {
            Directory.CreateDirectory(Core.AppDir);
            // 사용자 권한의 관리센터가 exe 를 바꿀 수 있게 (Users = S-1-5-32-545)
            Engine.Run(Engine.Sys32("icacls.exe"), "\"" + Core.AppDir + "\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q", 120000);
            var notes = new List<string>();
            ReleaseInfo r;
            try { r = Core.GetLatestPrinter(); }
            catch (Exception e) { throw new Exception("GitHub 에 닿지 못했습니다 — 인터넷 연결 후 관리센터에서 다시 설치하세요 (" + e.Message + ")"); }
            if (Core.IsUpToDate(r))
            {
                notes.Add("이미 최신 (" + r.Tag + ")");
                Core.StopApp(15000);   // 중앙제어 옮기기 전에 예전 판을 닫는다 (닫힐 때 중앙도 함께 끈다)
            }
            else
            {
                string dl = Path.Combine(Core.AppDir, Core.ExeName + ".download");
                Core.DownloadFile(r.Url, r.Size, r.Sha256, dl, delegate(long d, long t) { if (s.Progress != null) s.Progress("받는 중 " + Core.Mb(d) + " / " + Core.Mb(t)); });
                Core.ApplyUpdate(r, dl, s.Progress);
                notes.Add(r.Tag + " 설치");
            }
            if (s.Progress != null) s.Progress("예전 설정 옮기는 중");
            string moved = MigrateCentral();
            if (moved != null) notes.Add(moved);
            string auto = FixAutostart();
            if (auto != null) notes.Add(auto);
            s.Result = string.Join(" · ", notes.ToArray());
        }

        // 이 PC 가 '중앙' 이었다면 중앙 데이터는 예전 exe 옆 '중앙제어\데이터' (10-28 전 판은 '중앙데이터') 에 있다.
        // 앱은 자기 exe 옆만 보므로, 새 자리에서 처음 켜면 빈 중앙을 새로 만든다 — 그 전에 옮겨 둔다 (원본은 그대로 둠).
        const string CentralFolder = "중앙제어", CentralData = "데이터", OldCentralData = "중앙데이터";

        static string MigrateCentral()
        {
            string target = Path.Combine(Core.AppDir, CentralFolder);
            if (Directory.Exists(Path.Combine(target, CentralData))) { Core.Log("중앙제어: 새 자리에 이미 있음"); return null; }
            string src = FindOldCentral(CandidateFolders(), Core.AppDir);
            if (src == null) { Core.Log("중앙제어: 예전 데이터 없음"); return null; }
            if (RunningFrom(src))
                throw new Exception("예전 자리의 중앙(central.exe)이 켜져 있어 데이터를 옮기지 못했습니다 — 대시보드에서 중앙을 끈 뒤 다시 설치하세요 (" + src + ")");
            CopyCentral(src, target);
            Core.Log("중앙제어 데이터 옮김: " + src + " → " + target);
            return "중앙제어 데이터 옮김";
        }

        static List<string> CandidateFolders()
        {
            var list = new List<string>();
            string reg = RunValue();
            if (reg != null) { try { list.Add(Path.GetDirectoryName(reg.Trim().Trim('"'))); } catch { } }
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            return list;
        }

        public static string FindOldCentral(List<string> candidates, string exclude)
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c) || !Directory.Exists(c)) continue;
                if (string.Equals(Path.GetFullPath(c).TrimEnd('\\'), Path.GetFullPath(exclude).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var data in new[] { Path.Combine(c, CentralFolder, CentralData), Path.Combine(c, OldCentralData) })
                {
                    if (!Directory.Exists(data)) continue;
                    DateTime t = Newest(data);
                    if (t > bestTime) { bestTime = t; best = data; }
                    break;
                }
            }
            return best;
        }

        static DateTime Newest(string folder)
        {
            DateTime t = Directory.GetLastWriteTime(folder);
            try { foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) { var w = File.GetLastWriteTime(f); if (w > t) t = w; } }
            catch { }
            return t;
        }

        public static void CopyCentral(string data, string target)
        {
            string home = Path.GetDirectoryName(data);
            Directory.CreateDirectory(target);
            foreach (var f in new[] { "central.exe", "central.exe.json" })
            {
                string s = Path.Combine(home, f);
                if (File.Exists(s)) File.Copy(s, Path.Combine(target, f), true);
            }
            CopyTree(data, Path.Combine(target, CentralData));
        }

        static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        static bool RunningFrom(string data)
        {
            string home = Path.GetDirectoryName(data);
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.MainModule.FileName.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
            }
            return false;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "코어닷투데이 프린터서버";

        static string RunValue()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k == null ? null : k.GetValue(RunName) as string;
        }

        /// <summary>앱의 'Windows 시작 시 자동 실행' 이 켜져 있는데 예전 exe 를 가리키면 새 자리로. 꺼져 있으면 그대로.</summary>
        static string FixAutostart()
        {
            string cur = RunValue();
            if (cur == null) return null;
            string want = "\"" + Path.Combine(Core.AppDir, Core.ExeName) + "\"";
            if (string.Equals(cur.Trim(), want, StringComparison.OrdinalIgnoreCase)) return null;
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) k.SetValue(RunName, want);
            Core.Log("자동 실행 경로 변경: " + cur + " → " + want);
            return "자동 실행 경로 갱신";
        }

        // ================================================================ 제거
        public static int Uninstall()
        {
            if (!IsAdmin()) return Elevate("--uninstall");
            if (MessageBox.Show("코어닷 관리센터를 제거합니다.\r\n\r\n드라이버 · 프로그램 · 코어닷프린터는 그대로 둡니다 (각각 제어판에서 지울 수 있음).\r\n계속할까요?",
                "코어닷 관리센터 제거", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                svc.Connect();
                dynamic root = svc.GetFolder("\\");
                try { dynamic f = svc.GetFolder("\\CoreDot"); try { f.DeleteTask("Installer", 0); } catch { } try { f.DeleteTask("Check", 0); } catch { } root.DeleteFolder("CoreDot", 0); } catch { }
            }
            catch { }
            Core.TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "코어닷 관리센터.lnk"));
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "코어닷"), true); } catch { }
            try { using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) hk.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { Directory.Delete(Paths.DataDir, true); } catch { }
            // 돌고 있는 자기 자신은 지울 수 없으니 잠시 뒤 지운다
            var psi = new ProcessStartInfo(Engine.Sys32("cmd.exe"), "/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"" + Paths.ProgramDir + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
            MessageBox.Show("코어닷 관리센터를 제거했습니다.", "코어닷 관리센터");
            return 0;
        }

        static Form Splash(string text)
        {
            var f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.Size = new Size(380, 90);
            f.BackColor = Color.White;
            f.TopMost = true;
            var l = new Label();
            l.Text = text;
            l.Font = new Font("맑은 고딕", 11f);
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Dock = DockStyle.Fill;
            f.Controls.Add(l);
            return f;
        }
    }

    // ================================================================== 설치 창
    class SetupForm : Form
    {
        readonly List<SetupStep> steps;
        readonly Label[] states;
        readonly Label head;
        readonly Button go, close;
        readonly ProgressBar bar;
        bool running;

        public SetupForm(List<SetupStep> steps)
        {
            this.steps = steps;
            Text = "코어닷 관리센터 설치 " + Program.Version;
            Font = UI.Base;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            head = new Label();
            head.Text = "키오스크에 필요한 드라이버 · 프로그램을 한 번에 설치합니다";
            head.Font = UI.Title;
            head.SetBounds(20, 14, 620, 28);
            Controls.Add(head);
            var sub = new Label();
            sub.Text = "설치가 끝나면 '코어닷 관리센터' 가 새 버전 · 새 프로그램을 알아서 확인합니다. 프린터 USB 는 설치 후에 꽂아도 됩니다.";
            sub.ForeColor = UI.Muted;
            sub.Font = UI.Small;
            sub.SetBounds(20, 44, 620, 38);
            Controls.Add(sub);

            states = new Label[steps.Count];
            int y = 90;
            for (int i = 0; i < steps.Count; i++)
            {
                var n = new Label();
                n.Text = steps[i].Title;
                n.SetBounds(24, y, 250, 24);
                n.AutoEllipsis = true;
                Controls.Add(n);
                var s = new Label();
                s.ForeColor = UI.Muted;
                s.SetBounds(280, y, 360, 24);
                s.AutoEllipsis = true;
                Controls.Add(s);
                states[i] = s;
                y += 28;
            }
            bar = new ProgressBar();
            bar.SetBounds(20, y + 10, 620, 14);
            bar.Maximum = steps.Count;
            Controls.Add(bar);

            go = UI.Btn("설치", delegate { Start(); });
            go.Font = UI.Bold;
            close = UI.Btn("닫기", delegate { Close(); });
            var flow = new FlowLayoutPanel();
            flow.FlowDirection = FlowDirection.RightToLeft;
            flow.SetBounds(20, y + 36, 620, 40);
            flow.Controls.Add(close);
            flow.Controls.Add(go);
            Controls.Add(flow);
            AcceptButton = go;
            ClientSize = new Size(660, y + 86);
            FormClosing += delegate(object o, FormClosingEventArgs e) { if (running) e.Cancel = true; };
        }

        void Start()
        {
            running = true;
            go.Enabled = false; close.Enabled = false;
            head.Text = "설치하는 중입니다… 창을 닫지 마세요";
            head.ForeColor = SystemColors.ControlText;
            var t = new Thread(delegate()
            {
                bool all = SetupMode.RunSteps(steps, delegate(int i, string msg)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        states[i].Text = msg;
                        states[i].ForeColor = msg.StartsWith("✘") ? UI.Bad : msg.StartsWith("✔") ? UI.Ok : UI.Muted;
                        if (msg.StartsWith("✔") || msg.StartsWith("✘")) bar.Value = Math.Min(bar.Maximum, i + 1);
                    });
                });
                SetupMode.LaunchAfter(true);
                BeginInvoke((MethodInvoker)delegate
                {
                    running = false;
                    close.Enabled = true;
                    AcceptButton = close;
                    if (all)
                    {
                        head.Text = SetupMode.NeedReboot ? "설치 완료 — PC 를 다시 시작해 주세요" : "설치가 모두 끝났습니다";
                        head.ForeColor = UI.Ok;
                    }
                    else
                    {
                        head.Text = "일부를 설치하지 못했습니다 (✘ 확인) — 관리센터에서 다시 설치할 수 있습니다";
                        head.ForeColor = UI.Bad;
                        go.Text = "다시 시도";
                        go.Enabled = true;
                    }
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
