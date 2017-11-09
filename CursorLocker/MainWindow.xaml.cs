using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using Screen = System.Windows.Forms.Screen;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace CursorLocker {
    public partial class MainWindow {
        private readonly string _config;
        private readonly NotifyIcon _notifyIcon;
        private readonly SolidColorBrush _nameColor = new SolidColorBrush(Colors.DarkBlue);
        private readonly SolidColorBrush _errorColor = new SolidColorBrush(Colors.DarkRed);
        private readonly HashSet<string> _listeners = new HashSet<string>();
        private readonly int _pid = Process.GetCurrentProcess().Id;
        private volatile Tuple<uint, string> _curFocus;
        private readonly AutoResetEvent _curFocusEvent = new AutoResetEvent(false);
        private volatile bool _listenFocus;
        private readonly AutoResetEvent _listenFocusEvent = new AutoResetEvent(false);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private void OnFocusChangedHandler(object src, AutomationFocusChangedEventArgs args) {
            var element = src as AutomationElement;
            if (element == null) return;
            _curFocus = Tuple.Create((uint)element.Current.ProcessId, element.Current.Name);
            _curFocusEvent.Set();
        }

        private void BindCursor(Tuple<uint, string> param){
            var pid = (int)param.Item1;
            if (_pid == pid)
                return;
            var process = Process.GetProcessById(pid);
            var moduleName = "########";
            var fileName = "########";
            try {
                moduleName = process.MainModule.ModuleName;
                fileName = process.MainModule.FileName;
            }
            catch (Win32Exception){

            }
            var names = new HashSet<string>{
                param.Item2,    // window Name
                process.ProcessName,
                moduleName,
                fileName
            };
            param = null;
            //Console.WriteLine($@"    focus changed to {string.Join(", ", names)}");
            bool bound;
            lock (_listeners)
                bound = _listeners.Overlaps(names);
            var bounded = System.Windows.Forms.Cursor.Clip == Screen.PrimaryScreen.Bounds;
            if (bound && !bounded) {
                System.Windows.Forms.Cursor.Clip = Screen.PrimaryScreen.Bounds;
                //Console.WriteLine($@"    locking cursor. triggered by: {string.Join("", names)}");
            } else if (!bound && bounded) {
                System.Windows.Forms.Cursor.Clip = Rectangle.Empty;
                //Console.WriteLine(@"    unlocking cursor.");
            }
        }

        private static Tuple<uint, string> GetCurrentFocus(){
            var handle = GetForegroundWindow();
            var nChars = GetWindowTextLength(handle) + 1;
            var buff = new StringBuilder(nChars);
            var result = "########";
            if (GetWindowText(handle, buff, nChars) > 0) {
                result = buff.ToString();
            }
            GetWindowThreadProcessId(handle, out var pid);
            return Tuple.Create(pid, result);
        }

        public MainWindow(){
            InitializeComponent();
            var t1 = new Thread(() => {
                while (true){
                    _listenFocusEvent.WaitOne();
                    if (_listenFocus) {
                        Automation.AddAutomationFocusChangedEventHandler(OnFocusChangedHandler);
                        //Console.WriteLine(@"    Focus handler enabled");
                    }
                    else{
                        Automation.RemoveAutomationFocusChangedEventHandler(OnFocusChangedHandler);
                        //Console.WriteLine(@"    Focus handler disabled");
                    }
                }
            }) { IsBackground = true };
            var t2 = new Thread(() => {
                while (true){
                    if (!_curFocusEvent.WaitOne(3000)){
                        var tu = GetCurrentFocus();
                        BindCursor(tu);
                    }
                    while (_curFocus != null){
                        var tu = Tuple.Create(_curFocus.Item1, _curFocus.Item2);
                        _curFocus = null;
                        BindCursor(tu);
                    }
                }
            }){IsBackground = true};
            t1.Start();
            t2.Start();

            _notifyIcon = new NotifyIcon {
                Visible = false,
                Icon = SystemIcons.Application
            };
            _notifyIcon.Click += (sender, args) => {
                Show();
                WindowState = WindowState.Normal;
                _notifyIcon.Visible = false;
            };

            _config = $"{Environment.ExpandEnvironmentVariables("%AppData%")}\\CursorLocker\\data.txt";
            if (!File.Exists(_config))
                return;
            using (var file = File.OpenText(_config)){
                string line;
                while ((line = file.ReadLine()) != null)
                    AddBoxItem(line);
            }
        }

        private void AddBoxItem(string item) {
            int c;
            lock (_listeners) {
                c = _listeners.Count;
                _listeners.Add(item);
            }
            if (c == 0){
                _listenFocus = true;
                _listenFocusEvent.Set();
            }
            PathBox.Items.Add(new TextBlock { Text = item });
            PathText.Text = "";
            CheckPaths();
        }

        private void CheckPaths() {
            foreach (var itemObj in PathBox.Items) {
                var item = (TextBlock)itemObj;
                if (!Path.IsPathRooted(item.Text))
                    item.Foreground = _nameColor;
                else if (!File.Exists(item.Text))
                    item.Foreground = _errorColor;
            }
        }

        private void Browse_Click(object sender, RoutedEventArgs e){
            var dlg = new OpenFileDialog{
                FileName = "Executable",
                DefaultExt = ".exe",
                Filter = "Executables|*.exe;*.bat;*.cmd;*.vbs|All|*.*"
            };
            
            if (dlg.ShowDialog() == true)
                AddBoxItem(dlg.FileName);
        }

        private void Window_Closing(object sender, CancelEventArgs e){
            var path = Path.GetDirectoryName(_config);
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path ?? throw new DirectoryException($"Couldn't create persistance file '{_config}'."));
            File.WriteAllLines(_config, PathBox.Items.Cast<TextBlock>().Select(tb=>tb.Text));
        }

        private void Window_StateChanged(object sender, EventArgs e){
            if (WindowState != WindowState.Minimized) return;
            Hide();
            _notifyIcon.Visible = true;
        }

        private void Window_KeyUp(object sender, KeyEventArgs e) {
           if (e.Key == Key.F5)
                CheckPaths();
        }

        private void PathBox_MouseUp(object sender, MouseButtonEventArgs e){
            if(PathBox.SelectedIndex == -1)
                return;
            if (e.MouseDevice.DirectlyOver.GetType() == typeof(ScrollViewer)){
                PathBox.UnselectAll();
                PathText.Text = "";
                return;
            }
            PathText.Text = ((TextBlock)PathBox.SelectedItem).Text;
            PathText.SelectionStart = PathText.Text.Length;
        }

        private void PathText_KeyUp(object sender, KeyEventArgs e) {
            if (e.Key != Key.Enter && e.Key != Key.Return)
                return;
            if(PathText.Text.Length == 0)
                return;
            lock (_listeners){
                if (_listeners.Contains(PathText.Text))
                    return;
            }

            AddBoxItem(PathText.Text);
        }

        private void PathBox_KeyUp(object sender, KeyEventArgs e) {
            if(e.Key != Key.Delete && e.Key != Key.Back)
                return;
            var c = -1;
            foreach (var item in PathBox.SelectedItems.Cast<TextBlock>().ToList()){
                PathBox.Items.Remove(item);
                lock (_listeners){
                    _listeners.Remove(item.Text);
                    c = _listeners.Count;
                }
            }
            if (c == 0){
                _listenFocus = false;
                _listenFocusEvent.Set();
            }
            PathText.Text = "";
        }
    }
}
