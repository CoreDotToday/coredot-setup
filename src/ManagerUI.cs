// 관리센터 창 · 새 버전 알림 창 · 설치 진행 창 (사용자 권한)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CoreDot
{
    static class UI
    {
        public static readonly Font Base = new Font("맑은 고딕", 10f);
        public static readonly Font Bold = new Font("맑은 고딕", 10f, FontStyle.Bold);
        public static readonly Font Title = new Font("맑은 고딕", 13f, FontStyle.Bold);
        public static readonly Font Small = new Font("맑은 고딕", 9f);
        public static readonly Color Ok = Color.FromArgb(0, 120, 60);
        public static readonly Color Warn = Color.FromArgb(170, 95, 0);
        public static readonly Color Bad = Color.Firebrick;
        public static readonly Color Muted = Color.DimGray;

        public static Button Btn(string text, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(6, 2, 6, 2);
            b.Margin = new Padding(4, 0, 0, 0);
            b.Click += click;
            return b;
        }

        public static void Open(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args ?? "");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception e) { MessageBox.Show("열지 못했습니다: " + file + "\r\n" + e.Message, "코어닷 관리센터"); }
        }
    }

    // ================================================================== 설치 진행 창
    /// <summary>시스템 항목은 SYSTEM 서비스에, 코어닷프린터는 이 프로세스(사용자 권한)에서 설치하고 진행을 보여 준다.</summary>
    class ProgressForm : Form
    {
        readonly List<Catalog.Pending> jobs;
        readonly ReleaseInfo printer;
        readonly Dictionary<string, Label> states = new Dictionary<string, Label>();
        readonly Label head;
        readonly Button close;
        bool finished;
        public bool ManagerUpdated;

        public ProgressForm(List<Catalog.Pending> jobs, ReleaseInfo printer)
        {
            this.jobs = jobs; this.printer = printer;
            Text = "코어닷 관리센터 — 설치";
            Font = UI.Base;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;

            head = new Label();
            head.Text = "설치하는 중입니다… 창을 닫지 마세요.";
            head.Font = UI.Title;
            head.SetBounds(20, 16, 560, 30);
            Controls.Add(head);

            int y = 58;
            foreach (var j in jobs)
            {
                var n = new Label();
                n.Text = j.Title + "  " + (j.IsNew ? "(새로 추가) " + j.To : j.From + " → " + j.To);
                n.SetBounds(20, y, 300, 24);
                n.AutoEllipsis = true;
                Controls.Add(n);
                var s = new Label();
                s.Text = "기다리는 중";
                s.ForeColor = UI.Muted;
                s.SetBounds(324, y, 256, 24);
                s.AutoEllipsis = true;
                Controls.Add(s);
                states[j.Id] = s;
                y += 28;
            }
            close = UI.Btn("닫기", delegate { Close(); });
            close.Enabled = false;
            close.Location = new Point(500, y + 14);
            Controls.Add(close);
            ClientSize = new Size(600, y + 60);
            FormClosing += delegate(object o, FormClosingEventArgs e) { if (!finished) e.Cancel = true; };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var t = new Thread(Work);
            t.IsBackground = true;
            t.Start();
        }

        void Set(string id, string state, string msg)
        {
            Label l;
            if (!states.TryGetValue(id, out l)) return;
            BeginInvoke((MethodInvoker)delegate
            {
                l.Text = (state == "done" ? "✔ " : state == "error" ? "✘ " : "… ") + msg;
                l.ForeColor = state == "done" ? UI.Ok : state == "error" ? UI.Bad : UI.Muted;
            });
        }

        void Work()
        {
            int fails = 0;
            bool reboot = false;
            var sys = new List<string>();
            foreach (var j in jobs) if (j.Id != "coredotprinter") sys.Add(j.Id);

            if (sys.Count > 0)
            {
                try
                {
                    string rid = ServiceClient.Request("install", sys);
                    var until = DateTime.Now.AddMinutes(60);
                    Dictionary<string, object> st = null;
                    while (DateTime.Now < until)
                    {
                        Thread.Sleep(700);
                        st = ServiceClient.ReadStatus();
                        if (st == null || Core.Str(st, "request") != rid) continue;
                        var items = st["items"] as Dictionary<string, object>;
                        if (items != null)
                            foreach (var kv in items)
                            {
                                var d = kv.Value as Dictionary<string, object>;
                                Set(kv.Key, Core.Str(d, "state"), Core.Str(d, "message"));
                            }
                        if (st["done"] is bool && (bool)st["done"]) break;
                    }
                    if (st == null || Core.Str(st, "request") != rid || !(st["done"] is bool && (bool)st["done"]))
                        throw new Exception("설치 서비스가 응답하지 않습니다.");
                    if (Core.Str(st, "error") != null) throw new Exception(Core.Str(st, "error"));
                    reboot = st["reboot"] is bool && (bool)st["reboot"];
                    var its = st["items"] as Dictionary<string, object>;
                    foreach (var id in sys)
                    {
                        var d = its != null && its.ContainsKey(id) ? its[id] as Dictionary<string, object> : null;
                        string s = Core.Str(d, "state");
                        if (s != "done") { fails++; if (s != "error") Set(id, "error", "설치되지 않음"); }
                        else if (id == "manager") ManagerUpdated = true;
                    }
                }
                catch (Exception e)
                {
                    Core.Log("설치 요청 실패: " + e);
                    foreach (var id in sys) Set(id, "error", e.Message);
                    fails += sys.Count;
                }
            }

            foreach (var j in jobs)
            {
                if (j.Id != "coredotprinter") continue;
                try
                {
                    var r = printer ?? Core.GetLatestPrinter();
                    Core.UpdatePrinterApp(r, false, delegate(string s) { Set("coredotprinter", "installing", s); });
                    Set("coredotprinter", "done", r.Tag + " 설치 완료");
                }
                catch (Exception e)
                {
                    Core.Log("코어닷프린터 업데이트 실패: " + e);
                    Set("coredotprinter", "error", e.Message);
                    fails++;
                }
            }

            BeginInvoke((MethodInvoker)delegate
            {
                finished = true;
                close.Enabled = true;
                AcceptButton = close;
                if (fails == 0)
                {
                    head.Text = reboot ? "설치 완료 — PC 를 다시 시작해 주세요" : "설치가 끝났습니다";
                    head.ForeColor = UI.Ok;
                    if (!reboot)
                    {
                        var timer = new System.Windows.Forms.Timer();
                        timer.Interval = 4000;
                        timer.Tick += delegate { timer.Stop(); Close(); };
                        timer.Start();
                    }
                }
                else
                {
                    head.Text = fails + "개 항목을 설치하지 못했습니다";
                    head.ForeColor = UI.Bad;
                }
            });
        }
    }

    // ================================================================== 새 버전 · 새 프로그램 알림 (--check)
    class PopupForm : Form
    {
        readonly List<Catalog.Pending> pending;
        readonly List<CheckBox> checks = new List<CheckBox>();
        public List<Catalog.Pending> Chosen = new List<Catalog.Pending>();

        public PopupForm(List<Catalog.Pending> pending)
        {
            this.pending = pending;
            Text = "코어닷 관리센터";
            Font = UI.Base;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;   // 키오스크 전체 화면 위로
            ShowInTaskbar = true;

            var title = new Label();
            title.Text = "새로 설치하거나 업데이트할 것이 있습니다";
            title.Font = UI.Title;
            title.SetBounds(20, 16, 520, 30);
            Controls.Add(title);

            int y = 58;
            foreach (var p in pending)
            {
                var c = new CheckBox();
                c.Checked = true;
                c.Text = p.IsNew ? p.Title + "  —  새로 추가 (" + p.To + ")" : p.Title + "  —  " + p.From + " → " + p.To;
                c.SetBounds(24, y, 510, 26);
                c.Tag = p;
                checks.Add(c);
                Controls.Add(c);
                y += 28;
            }
            var warn = new Label();
            warn.Text = "설치하는 동안 코어닷프린터가 잠깐 꺼졌다 켜질 수 있습니다. 인쇄 중이 아닐 때 눌러 주세요.";
            warn.ForeColor = UI.Warn;
            warn.SetBounds(20, y + 8, 520, 40);
            Controls.Add(warn);

            var later = UI.Btn("나중에", delegate { DialogResult = DialogResult.Cancel; Close(); });
            var ok = UI.Btn("지금 설치", delegate
            {
                foreach (var c in checks) if (c.Checked) Chosen.Add((Catalog.Pending)c.Tag);
                DialogResult = Chosen.Count > 0 ? DialogResult.OK : DialogResult.Cancel;
                Close();
            });
            ok.Font = UI.Bold;
            var bar = new FlowLayoutPanel();
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.SetBounds(20, y + 56, 520, 40);
            bar.Controls.Add(later);
            bar.Controls.Add(ok);
            Controls.Add(bar);
            AcceptButton = ok;
            CancelButton = later;
            ClientSize = new Size(560, y + 104);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
        }
    }

    // ================================================================== 관리센터 창
    class HubForm : Form
    {
        readonly Panel rows;
        readonly Label info;
        CatalogData cat;
        ReleaseInfo printer;
        List<Catalog.Pending> pending = new List<Catalog.Pending>();
        bool busy;

        public HubForm()
        {
            Text = "코어닷 관리센터 " + Program.Version;
            Font = UI.Base;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(940, 480);
            Size = new Size(980, 640);

            var top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 64;
            var title = new Label();
            title.Text = "코어닷 관리센터";
            title.Font = UI.Title;
            title.SetBounds(18, 12, 300, 28);
            top.Controls.Add(title);
            info = new Label();
            info.ForeColor = UI.Muted;
            info.Font = UI.Small;
            info.SetBounds(20, 40, 520, 20);
            top.Controls.Add(info);
            var actions = new FlowLayoutPanel();
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.Dock = DockStyle.Right;
            actions.Width = 360;
            actions.Padding = new Padding(0, 16, 14, 0);
            actions.Controls.Add(UI.Btn("모두 설치 · 업데이트", delegate { InstallAll(); }));
            actions.Controls.Add(UI.Btn("새 버전 확인", delegate { Reload(); }));
            top.Controls.Add(actions);

            var bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 44;
            bottom.Padding = new Padding(14, 8, 14, 0);
            var tl = new Label();
            tl.Text = "문제 해결:";
            tl.AutoSize = true;
            tl.ForeColor = UI.Muted;
            tl.Margin = new Padding(0, 6, 4, 0);
            bottom.Controls.Add(tl);
            bottom.Controls.Add(UI.Btn("프린터 설정 열기", delegate { UI.Open("ms-settings:printers", null); }));
            bottom.Controls.Add(UI.Btn("인쇄 대기열 비우기", delegate { ClearSpool(); }));
            bottom.Controls.Add(UI.Btn("로그 폴더", delegate { UI.Open("explorer.exe", "\"" + Path.GetDirectoryName(Paths.ServiceLog) + "\""); }));

            rows = new Panel();
            rows.Dock = DockStyle.Fill;
            rows.AutoScroll = true;
            rows.BackColor = SystemColors.Window;

            Controls.Add(rows);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Reload();
        }

        void Reload()
        {
            if (busy) return;
            busy = true;
            info.Text = "확인하는 중…";
            var t = new Thread(delegate()
            {
                string err = null;
                try { cat = Catalog.Fetch(); } catch (Exception ex) { err = ex.Message; cat = null; }
                try { printer = Core.GetLatestPrinter(); } catch (Exception ex) { Core.Log("코어닷프린터 확인 실패: " + ex.Message); printer = null; }
                var p = Catalog.ComputePending(cat, printer, Program.Version);
                var printers = Catalog.Printers();
                var have = new Dictionary<string, string>();
                if (cat != null) foreach (var it in cat.Items) have[it.Id] = Catalog.InstalledVersion(it);
                string app = Core.InstalledVersion();
                bool running = Core.FindAppProcesses().Count > 0;
                BeginInvoke((MethodInvoker)delegate
                {
                    busy = false;
                    pending = p;
                    Build(have, printers, app, running);
                    if (err != null) info.Text = err;
                    else info.Text = (cat != null && cat.FromCache ? "인터넷 연결 안 됨 — 마지막으로 받은 목록 · " : "")
                        + (p.Count == 0 ? "모두 최신입니다" : p.Count + "개 항목을 설치하거나 업데이트할 수 있습니다")
                        + "  ·  " + DateTime.Now.ToString("HH:mm") + " 확인";
                    info.ForeColor = err != null ? UI.Bad : p.Count == 0 ? UI.Ok : UI.Warn;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        Catalog.Pending PendingFor(string id)
        {
            foreach (var p in pending) if (p.Id == id) return p;
            return null;
        }

        void Build(Dictionary<string, string> have, List<Catalog.PrinterInfo> printers, string app, bool running)
        {
            rows.SuspendLayout();
            rows.Controls.Clear();
            int y = 0;

            // 코어닷프린터
            {
                var p = PendingFor("coredotprinter");
                string status, sub = app == null ? "설치 안 됨" : app;
                Color col;
                if (app == null) { status = "설치 안 됨"; col = UI.Bad; }
                else if (p != null) { status = "업데이트 있음 → " + p.To; col = UI.Warn; }
                else { status = running ? "최신 · 실행 중" : "최신 · 꺼져 있음"; col = UI.Ok; }
                var btns = new List<Button>();
                if (app != null) btns.Add(UI.Btn("열기", delegate { Core.StartApp(false); }));
                btns.Add(ActionButton("coredotprinter", app == null ? "설치" : p != null ? "업데이트" : null, "코어닷프린터", app, printer == null ? null : printer.Tag));
                y = AddRow(y, "코어닷프린터", sub, status, col, btns);
            }

            if (cat != null)
            {
                foreach (var it in cat.Items)
                {
                    string v = have.ContainsKey(it.Id) ? have[it.Id] : null;
                    var p = PendingFor(it.Id);
                    string status;
                    Color col;
                    var btns = new List<Button>();
                    var mine = new List<Catalog.PrinterInfo>();
                    foreach (var pr in printers) foreach (var dn in it.Printers) if (string.Equals(pr.Driver, dn, StringComparison.OrdinalIgnoreCase)) mine.Add(pr);

                    if (v == null) { status = "설치 안 됨"; col = UI.Bad; }
                    else if (p != null) { status = "업데이트 있음 → " + p.To; col = UI.Warn; }
                    else if (it.Kind == "driver")
                    {
                        int on = 0;
                        foreach (var pr in mine) if (!pr.Offline) on++;
                        if (mine.Count == 0) { status = "설치됨 · USB 를 꽂으면 프린터가 생김"; col = UI.Muted; }
                        else if (on == mine.Count) { status = "프린터 " + on + "대 연결됨"; col = UI.Ok; }
                        else { status = "프린터 " + mine.Count + "대 중 " + on + "대 연결됨"; col = UI.Warn; }
                    }
                    else { status = "최신"; col = UI.Ok; }

                    if (it.Kind == "driver" && mine.Count > 0)
                    {
                        var first = mine[0].Name;
                        if (mine.Count == 1) btns.Add(UI.Btn("인쇄 설정", delegate { UI.Open("rundll32.exe", "printui.dll,PrintUIEntry /e /n \"" + first + "\""); }));
                        else btns.Add(UI.Btn("프린터 " + mine.Count + "대", delegate { UI.Open("ms-settings:printers", null); }));
                    }
                    if (!string.IsNullOrEmpty(it.Open) && File.Exists(it.Open)) { string o = it.Open; btns.Add(UI.Btn("열기", delegate { UI.Open(o, null); })); }
                    foreach (var tool in it.Tools)
                    {
                        if (string.IsNullOrEmpty(tool.Path) || !File.Exists(tool.Path)) continue;
                        var tt = tool;
                        btns.Add(UI.Btn(tt.Label ?? "도구", delegate { UI.Open(tt.Path, tt.Args); }));
                    }
                    btns.Add(ActionButton(it.Id, v == null ? "설치" : p != null ? "업데이트" : "다시 설치", it.Name, v, it.Version));
                    string sub = (it.Kind == "driver" ? "드라이버 " : "") + (v ?? "—") + (v != null && it.Version != null && p == null ? "" : "  (최신 " + it.Version + ")");
                    y = AddRow(y, it.Name, sub, status, col, btns);
                }
            }

            {
                var p = PendingFor("manager");
                if (p != null)
                    y = AddRow(y, "코어닷 관리센터", Program.Version, "업데이트 있음 → " + p.To, UI.Warn,
                        new List<Button> { ActionButton("manager", "업데이트", "코어닷 관리센터", Program.Version, p.To) });
            }
            rows.ResumeLayout();
        }

        Button ActionButton(string id, string text, string title, string from, string to)
        {
            if (text == null) text = "다시 받기";
            return UI.Btn(text, delegate
            {
                var job = new Catalog.Pending { Id = id, Title = title, From = from, To = to };
                if (id == "coredotprinter" && text == "다시 받기")
                {
                    MessageBox.Show(this, "코어닷프린터는 이미 최신입니다.", "코어닷 관리센터");
                    return;
                }
                RunJobs(new List<Catalog.Pending> { job });
            });
        }

        int AddRow(int y, string name, string sub, string status, Color col, List<Button> btns)
        {
            var row = new Panel();
            row.SetBounds(0, y, rows.ClientSize.Width, 58);
            row.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            var n = new Label();
            n.Text = name;
            n.Font = UI.Bold;
            n.SetBounds(18, 8, 230, 22);
            n.AutoEllipsis = true;
            row.Controls.Add(n);
            var s = new Label();
            s.Text = sub;
            s.Font = UI.Small;
            s.ForeColor = UI.Muted;
            s.SetBounds(18, 31, 230, 20);
            s.AutoEllipsis = true;
            row.Controls.Add(s);
            var st = new Label();
            st.Text = status;
            st.ForeColor = col;
            const int flowW = 440;
            st.SetBounds(254, 18, Math.Max(160, row.Width - flowW - 270), 22);
            st.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            st.AutoEllipsis = true;
            row.Controls.Add(st);
            var flow = new FlowLayoutPanel();
            flow.FlowDirection = FlowDirection.RightToLeft;
            flow.WrapContents = false;
            flow.SetBounds(row.Width - flowW - 14, 13, flowW, 34);
            flow.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            for (int i = btns.Count - 1; i >= 0; i--) flow.Controls.Add(btns[i]);
            row.Controls.Add(flow);
            var line = new Label();
            line.BorderStyle = BorderStyle.Fixed3D;
            line.SetBounds(12, 56, row.Width - 24, 2);
            line.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            row.Controls.Add(line);
            rows.Controls.Add(row);
            return y + 58;
        }

        void InstallAll()
        {
            if (pending.Count == 0) { MessageBox.Show(this, "모두 최신입니다.", "코어닷 관리센터"); return; }
            RunJobs(new List<Catalog.Pending>(pending));
        }

        void RunJobs(List<Catalog.Pending> jobs)
        {
            using (var f = new ProgressForm(jobs, printer))
            {
                f.ShowDialog(this);
                if (f.ManagerUpdated)
                {
                    MessageBox.Show(this, "관리센터가 새 버전으로 바뀌었습니다. 다시 엽니다.", "코어닷 관리센터");
                    Process.Start(Paths.ManagerExe);
                    Close();
                    return;
                }
            }
            Reload();
        }

        void ClearSpool()
        {
            if (MessageBox.Show(this, "인쇄 대기 중인 문서를 모두 지우고 인쇄 서비스를 다시 시작합니다.\r\n인쇄 중이 아닐 때 하세요. 계속할까요?",
                "인쇄 대기열 비우기", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                string rid = ServiceClient.Request("clear-spool", new List<string>());
                var until = DateTime.Now.AddSeconds(90);
                while (DateTime.Now < until)
                {
                    Thread.Sleep(700);
                    var st = ServiceClient.ReadStatus();
                    if (st != null && Core.Str(st, "request") == rid && st["done"] is bool && (bool)st["done"])
                    {
                        MessageBox.Show(this, Core.Str(st, "error") ?? "인쇄 대기열을 비웠습니다.", "코어닷 관리센터");
                        return;
                    }
                }
                MessageBox.Show(this, "응답이 없습니다. 잠시 뒤 다시 해 보세요.", "코어닷 관리센터");
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "코어닷 관리센터"); }
        }
    }
}
