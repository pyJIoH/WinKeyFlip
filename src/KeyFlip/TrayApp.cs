using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyFlip
{
    /// The tray icon, its menu and the application's lifetime.
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Engine engine = new Engine();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly SynchronizationContext context;
        private readonly ToolStripMenuItem hintItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem statusItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem pauseItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem macItem = new ToolStripMenuItem("Mac shortcuts");
        private readonly Icon activeIcon;
        private readonly Icon pausedIcon;
        private readonly RegisteredWaitHandle showRegistration;
        private readonly RegisteredWaitHandle quitRegistration;
        private SettingsForm settings;
        private int updatePending;
        private bool closing;

        public TrayApp(EventWaitHandle showSettings, EventWaitHandle quit)
        {
            // Not SynchronizationContext.Current: by now the menu items above have made
            // the framework install a plain context that would run posted work on
            // thread pool threads instead of this one.
            context = new WindowsFormsSynchronizationContext();
            int size = SystemInformation.SmallIconSize.Width;
            activeIcon = AppIcon.Create(size, true);
            pausedIcon = AppIcon.Create(size, false);

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem title = new ToolStripMenuItem("KeyFlip");
            title.Font = new Font(title.Font, FontStyle.Bold);
            title.Click += delegate { ShowSettings(); };
            menu.Items.Add(title);
            hintItem.Enabled = false;
            menu.Items.Add(hintItem);
            statusItem.Enabled = false;
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            pauseItem.Click += delegate { engine.ToggleEnabled(); };
            menu.Items.Add(pauseItem);
            macItem.Click += delegate { engine.SaveMacShortcuts(!engine.MacShortcuts); };
            menu.Items.Add(macItem);
            menu.Items.Add(new ToolStripMenuItem("Settings…", null, delegate { ShowSettings(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Quit KeyFlip", null, delegate { Quit(); }));

            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowSettings();
            };

            engine.StateChanged += StateChanged;
            engine.Start();
            UpdateState();
            tray.Visible = true;

            timer.Interval = 1000;
            timer.Tick += delegate { engine.Tick(); };
            timer.Start();

            // Keys that are held when the screen is locked or the computer goes to
            // sleep are released where KeyFlip cannot see it.
            SystemEvents.SessionSwitch += SessionSwitched;
            SystemEvents.PowerModeChanged += PowerModeChanged;

            // A second copy of KeyFlip signals these events instead of starting.
            showRegistration = ThreadPool.RegisterWaitForSingleObject(showSettings, delegate
            {
                context.Post(delegate { ShowSettings(); }, null);
            }, null, Timeout.Infinite, false);
            quitRegistration = ThreadPool.RegisterWaitForSingleObject(quit, delegate
            {
                context.Post(delegate { Quit(); }, null);
            }, null, Timeout.Infinite, false);

            if (!Settings.HasOpened)
            {
                Settings.HasOpened = true;
                ShowSettings();
            }
        }

        private void SessionSwitched(object sender, SessionSwitchEventArgs e)
        {
            engine.InputInterrupted();
        }

        private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            engine.InputInterrupted();
        }

        private void StopWatchingSession()
        {
            SystemEvents.SessionSwitch -= SessionSwitched;
            SystemEvents.PowerModeChanged -= PowerModeChanged;
        }

        /// May be called on any thread, many times a second.
        private void StateChanged()
        {
            if (Interlocked.Exchange(ref updatePending, 1) == 1) return;
            context.Post(delegate
            {
                Interlocked.Exchange(ref updatePending, 0);
                UpdateState();
            }, null);
        }

        private void UpdateState()
        {
            if (closing) return;
            string status = engine.Status;
            tray.Icon = engine.Running ? activeIcon : pausedIcon;
            // The tooltip of a tray icon is limited to 63 characters.
            string tip = "KeyFlip — " + status;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
            hintItem.Text = engine.LayoutKeyName + ": layout  ·  Left Ctrl: case";
            statusItem.Text = status.Length > 90 ? status.Substring(0, 89) + "…" : status;
            pauseItem.Text = engine.Enabled ? "Pause" : "Resume";
            macItem.Checked = engine.MacShortcuts;
            if (settings != null) settings.UpdateState();
        }

        private void ShowSettings()
        {
            if (closing) return;
            if (settings == null)
            {
                settings = new SettingsForm(engine, Quit);
                settings.FormClosed += delegate { settings = null; };
                settings.Show();
            }
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
            Native.SetForegroundWindow(settings.Handle);
        }

        private void Quit()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            StopWatchingSession();
            showRegistration.Unregister(null);
            quitRegistration.Unregister(null);
            engine.StateChanged -= StateChanged;
            tray.Visible = false;
            if (settings != null) settings.Close();
            engine.Shutdown();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!closing)
                {
                    closing = true;
                    StopWatchingSession();
                    engine.Shutdown();
                }
                tray.Dispose();
                timer.Dispose();
                activeIcon.Dispose();
                pausedIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
