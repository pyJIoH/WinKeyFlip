using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using KeyFlip.Core;

namespace KeyFlip
{
    /// The settings window. Every position is given for 96 DPI and scaled by hand:
    /// mixing the framework's automatic scaling with self-sizing containers scales
    /// some of them twice.
    internal sealed class SettingsForm : Form
    {
        private const int ContentWidth = 500;
        private const int SideMargin = 22;

        private sealed class LayoutItem
        {
            public KeyboardLayout Layout;

            public override string ToString()
            {
                return Layout.Name;
            }
        }

        private readonly Engine engine;
        private readonly Action quit;
        private readonly float scale;
        private readonly List<Control> blocks = new List<Control>();
        private readonly List<int> gaps = new List<int>();
        private readonly Label badge = new Label();
        private readonly Label layoutKeycap = new Label();
        private readonly Label layoutTitle = new Label();
        private readonly Label description;
        private readonly RadioButton altKey = new RadioButton();
        private readonly RadioButton winKey = new RadioButton();
        private readonly ComboBox english = new ComboBox();
        private readonly ComboBox russian = new ComboBox();
        private readonly GroupBox layoutsGroup = new GroupBox();
        private readonly Label layoutWarning = new Label();
        private readonly CheckBox capsLock = new CheckBox();
        private readonly CheckBox launchAtLogin = new CheckBox();
        private readonly ComboBox testWindow = new ComboBox();
        private readonly Label currentLayout = new Label();
        private readonly Label shortcutStatus = new Label();
        private readonly Label status = new Label();
        private readonly Button pause = new Button();
        private readonly Color secondary = Color.FromArgb(96, 96, 104);
        private bool updating;

        /// For checks that draw the window without taking the focus.
        internal bool ShowInactive { get; set; }

        public SettingsForm(Engine engine, Action quit)
        {
            this.engine = engine;
            this.quit = quit;

            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Font = SystemFonts.MessageBoxFont;
            Text = "KeyFlip";
            Icon = AppIcon.Create(32, true);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SystemColors.Window;
            KeyPreview = true;
            using (Graphics graphics = CreateGraphics()) scale = graphics.DpiX / 96F;

            Add(Header(), 12);
            Add(Gestures(), 12);
            description = Paragraph(DescriptionText());
            Add(description, 14);
            Add(LayoutsGroup(), 12);
            Add(TapKeyRow(), 6);
            capsLock.Text = "Caps Lock only switches the layout (hold it for the regular Caps Lock)";
            capsLock.AutoSize = true;
            capsLock.CheckedChanged += CapsLockChanged;
            Add(capsLock, 6);
            launchAtLogin.Text = "Start when I sign in";
            launchAtLogin.AutoSize = true;
            launchAtLogin.CheckedChanged += LaunchAtLoginChanged;
            Add(launchAtLogin, 12);
            Add(TestGroup(), 12);
            status.AutoSize = false;
            status.Size = new Size(S(ContentWidth), S(50));
            status.UseMnemonic = false;
            Add(status, 6);
            Add(Footer(), 0);
            ResumeLayout(false);

            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
            };
            ReloadLayouts();
            ReloadTestWindows();
            updating = true;
            altKey.Checked = engine.LayoutTapKey == LayoutTapKey.Alt;
            winKey.Checked = engine.LayoutTapKey == LayoutTapKey.Win;
            capsLock.Checked = engine.CapsLockSwitchesLayout;
            launchAtLogin.Checked = Settings.LaunchAtLogin;
            updating = false;
            UpdateState();
            Arrange();
        }

        protected override bool ShowWithoutActivation
        {
            get { return ShowInactive; }
        }

        // ---- Building the window ----

        /// Pixels at the current scale for a length given at 96 DPI.
        private int S(int value)
        {
            return (int)Math.Round(value * scale);
        }

        private void Add(Control block, int gapBelow)
        {
            blocks.Add(block);
            gaps.Add(gapBelow);
            Controls.Add(block);
        }

        /// Stacks the blocks from top to bottom and sizes the window around them.
        private void Arrange()
        {
            int y = S(18);
            for (int i = 0; i < blocks.Count; i++)
            {
                blocks[i].Location = new Point(S(SideMargin), y);
                y += blocks[i].Height + S(gaps[i]);
            }
            ClientSize = new Size(S(ContentWidth + 2 * SideMargin), y + S(18));
        }

        private Label Paragraph(string text)
        {
            Label label = new Label();
            label.Font = Font;
            label.Text = text;
            label.ForeColor = secondary;
            label.AutoSize = false;
            label.UseMnemonic = false;
            label.Size = new Size(S(ContentWidth), label.GetPreferredSize(new Size(S(ContentWidth), 0)).Height);
            return label;
        }

        private Label Caption(string text, int x, int y, Font font, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(S(x), S(y));
            label.AutoSize = true;
            label.ForeColor = color;
            if (font != null) label.Font = font;
            label.UseMnemonic = false;
            return label;
        }

        private GroupBox Group(GroupBox group, string title, int height)
        {
            group.Text = title;
            group.Size = new Size(S(ContentWidth), S(height));
            return group;
        }

        private Button Push(string text, int x, int y, int width, EventHandler click)
        {
            Button button = new Button();
            button.Text = text;
            button.Bounds = new Rectangle(S(x), S(y), S(width), S(27));
            button.UseVisualStyleBackColor = true;
            button.Click += click;
            return button;
        }

        private Control Header()
        {
            Panel panel = new Panel();
            panel.Size = new Size(S(ContentWidth), S(52));

            PictureBox picture = new PictureBox();
            picture.Image = AppIcon.Draw(S(44), true);
            picture.SizeMode = PictureBoxSizeMode.Normal;
            picture.Bounds = new Rectangle(0, S(4), S(44), S(44));
            panel.Controls.Add(picture);
            panel.Controls.Add(Caption("KeyFlip", 54, 0, new Font(Font.FontFamily, 16F, FontStyle.Bold), SystemColors.ControlText));
            panel.Controls.Add(Caption("Two taps. The text you meant to type.", 57, 31, null, secondary));

            badge.AutoSize = false;
            badge.Bounds = new Rectangle(S(ContentWidth - 76), S(12), S(76), S(24));
            badge.TextAlign = ContentAlignment.MiddleCenter;
            badge.Font = new Font(Font.FontFamily, 8F, FontStyle.Bold);
            panel.Controls.Add(badge);
            return panel;
        }

        private Control Gestures()
        {
            GroupBox group = Group(new GroupBox(), "", 134);
            Font bold = new Font(Font, FontStyle.Bold);
            Font mono = new Font("Consolas", 9.5F);
            AddGesture(group, 20, layoutKeycap, layoutTitle, "ghbdtn  ↔  привет", bold, mono);
            Label line = new Label();
            line.AutoSize = false;
            line.BackColor = Color.FromArgb(224, 224, 228);
            line.Bounds = new Rectangle(S(14), S(70), S(ContentWidth - 28), Math.Max(1, S(1)));
            group.Controls.Add(line);
            Label controlKeycap = new Label();
            controlKeycap.Text = "Ctrl";
            Label controlTitle = new Label();
            controlTitle.Text = "Left Ctrl · Invert letter case";
            AddGesture(group, 81, controlKeycap, controlTitle, "Hello  ↔  hELLO", bold, mono);
            return group;
        }

        private void AddGesture(GroupBox group, int y, Label keycap, Label title, string example, Font bold, Font mono)
        {
            keycap.AutoSize = false;
            keycap.Bounds = new Rectangle(S(14), S(y), S(54), S(40));
            keycap.TextAlign = ContentAlignment.MiddleCenter;
            keycap.BackColor = Color.FromArgb(238, 239, 243);
            keycap.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            group.Controls.Add(keycap);
            title.Location = new Point(S(82), S(y + 1));
            title.AutoSize = true;
            title.Font = bold;
            title.UseMnemonic = false;
            group.Controls.Add(title);
            group.Controls.Add(Caption(example, 82, y + 21, mono, secondary));
        }

        private string DescriptionText()
        {
            return "Tap either " + engine.LayoutKeyName + " key alone to convert layout, or left Ctrl alone to invert case. "
                + "Selected text takes priority; otherwise KeyFlip changes the word before the cursor. "
                + "Normal keyboard shortcuts keep working.";
        }

        /// The gesture panel and the description name the key that converts the layout.
        private void UpdateKeyTexts()
        {
            string key = engine.LayoutKeyName;
            if (layoutKeycap.Text == key) return;
            layoutKeycap.Text = key;
            layoutTitle.Text = key + " · Change keyboard layout";
            description.Text = DescriptionText();
        }

        /// The choice between Alt and the Windows key for the layout gesture.
        private Control TapKeyRow()
        {
            Panel panel = new Panel();
            panel.Font = Font;
            panel.Size = new Size(S(ContentWidth), S(24));
            Label caption = Caption("Convert layout with a tap of", 0, 3, null, SystemColors.ControlText);
            panel.Controls.Add(caption);
            altKey.Text = "Alt";
            winKey.Text = "Win (Start; Option on a Mac keyboard)";
            int x = caption.PreferredSize.Width + S(10);
            foreach (RadioButton button in new[] { altKey, winKey })
            {
                button.AutoSize = true;
                button.UseMnemonic = false;
                button.Location = new Point(x, S(1));
                button.CheckedChanged += TapKeyChanged;
                panel.Controls.Add(button);
                x += button.PreferredSize.Width + S(14);
            }
            return panel;
        }

        private Control LayoutsGroup()
        {
            GroupBox group = Group(layoutsGroup, "Keyboard layouts", 128);
            group.Controls.Add(Caption("English", 14, 28, null, SystemColors.ControlText));
            group.Controls.Add(Caption("Russian", 14, 59, null, SystemColors.ControlText));
            foreach (ComboBox box in new[] { english, russian })
            {
                box.DropDownStyle = ComboBoxStyle.DropDownList;
                box.Location = new Point(S(90), S(box == english ? 24 : 55));
                box.Width = S(ContentWidth - 104);
                box.SelectedIndexChanged += LayoutChosen;
                group.Controls.Add(box);
            }
            group.Controls.Add(Push("Language settings…", 90, 87, 150, OpenLanguageSettings));
            group.Controls.Add(Push("Refresh", 246, 87, 84, delegate
            {
                engine.RefreshLayouts();
                ReloadLayouts();
                Arrange();
            }));
            layoutWarning.Font = Font;
            layoutWarning.Text = "Add an English and a Russian keyboard layout in Windows language settings, then press Refresh.";
            layoutWarning.ForeColor = Color.FromArgb(176, 92, 0);
            layoutWarning.AutoSize = false;
            layoutWarning.UseMnemonic = false;
            layoutWarning.Location = new Point(S(14), S(122));
            layoutWarning.Size = new Size(S(ContentWidth - 28),
                layoutWarning.GetPreferredSize(new Size(S(ContentWidth - 28), 0)).Height);
            layoutWarning.Visible = false;
            group.Controls.Add(layoutWarning);
            return group;
        }

        private Control TestGroup()
        {
            GroupBox group = Group(new GroupBox(), "Test conversion", 122);
            group.Controls.Add(Caption("Window", 14, 28, null, SystemColors.ControlText));
            testWindow.DropDownStyle = ComboBoxStyle.DropDownList;
            testWindow.Location = new Point(S(90), S(24));
            testWindow.Width = S(ContentWidth - 104);
            testWindow.DropDown += delegate { ReloadTestWindows(); };
            group.Controls.Add(testWindow);
            group.Controls.Add(Push("Convert layout", 90, 56, 120, delegate { Apply(KeyAction.Layout); }));
            group.Controls.Add(Push("Invert case", 216, 56, 100, delegate { Apply(KeyAction.InvertCase); }));
            currentLayout.AutoSize = false;
            currentLayout.AutoEllipsis = true;
            currentLayout.UseMnemonic = false;
            currentLayout.Bounds = new Rectangle(S(322), S(56), S(ContentWidth - 336), S(27));
            currentLayout.TextAlign = ContentAlignment.MiddleRight;
            currentLayout.ForeColor = secondary;
            group.Controls.Add(currentLayout);
            shortcutStatus.AutoSize = true;
            shortcutStatus.Location = new Point(S(14), S(93));
            shortcutStatus.ForeColor = secondary;
            group.Controls.Add(shortcutStatus);
            return group;
        }

        private Control Footer()
        {
            Panel panel = new Panel();
            panel.Size = new Size(S(ContentWidth), S(30));
            panel.Controls.Add(Caption("Works offline. Typed text is never saved.", 0, 7, null, secondary));
            pause.Bounds = new Rectangle(S(ContentWidth - 176), S(1), S(84), S(27));
            pause.UseVisualStyleBackColor = true;
            pause.Click += delegate { engine.ToggleEnabled(); };
            panel.Controls.Add(pause);
            panel.Controls.Add(Push("Quit", ContentWidth - 84, 1, 84, delegate { quit(); }));
            return panel;
        }

        // ---- Behaviour ----

        private void ReloadLayouts()
        {
            updating = true;
            try
            {
                Fill(english, "en", engine.Layouts.EnglishId);
                Fill(russian, "ru", engine.Layouts.RussianId);
                bool missing = english.Items.Count == 0 || russian.Items.Count == 0;
                layoutWarning.Visible = missing;
                layoutsGroup.Height = S(128) + (missing ? layoutWarning.Height + S(6) : 0);
            }
            finally
            {
                updating = false;
            }
        }

        private void Fill(ComboBox box, string language, uint selected)
        {
            box.Items.Clear();
            foreach (KeyboardLayout layout in engine.Layouts.Available)
            {
                if (layout.Language != language) continue;
                LayoutItem item = new LayoutItem();
                item.Layout = layout;
                box.Items.Add(item);
                if (layout.Id == selected) box.SelectedItem = item;
            }
            box.Enabled = box.Items.Count > 0;
        }

        private void LayoutChosen(object sender, EventArgs e)
        {
            if (updating) return;
            LayoutItem first = english.SelectedItem as LayoutItem;
            LayoutItem second = russian.SelectedItem as LayoutItem;
            engine.SaveLayouts(first == null ? 0 : first.Layout.Id, second == null ? 0 : second.Layout.Id);
        }

        private void OpenLanguageSettings(object sender, EventArgs e)
        {
            try
            {
                Process.Start("ms-settings:regionlanguage");
            }
            catch (Exception)
            {
                status.Text = "Open Settings, Time & language, Language & region to add a layout.";
            }
        }

        private void TapKeyChanged(object sender, EventArgs e)
        {
            // Both buttons report the change; act on the one that became checked.
            if (updating || !((RadioButton)sender).Checked) return;
            engine.SaveLayoutTapKey(winKey.Checked ? LayoutTapKey.Win : LayoutTapKey.Alt);
            UpdateKeyTexts();
        }

        private void CapsLockChanged(object sender, EventArgs e)
        {
            if (updating) return;
            engine.SaveCapsLockSwitchesLayout(capsLock.Checked);
        }

        private void LaunchAtLoginChanged(object sender, EventArgs e)
        {
            if (updating) return;
            try
            {
                Settings.SetLaunchAtLogin(launchAtLogin.Checked);
            }
            catch (Exception error)
            {
                status.Text = "Start when I sign in: " + error.Message;
            }
            updating = true;
            launchAtLogin.Checked = Settings.LaunchAtLogin;
            updating = false;
        }

        private string LastActiveText()
        {
            string title = engine.LastExternalTitle;
            return "Last active: " + (title.Length == 0 ? "none yet" : title);
        }

        private void ReloadTestWindows()
        {
            WindowInfo chosen = testWindow.SelectedItem as WindowInfo;
            testWindow.BeginUpdate();
            try
            {
                testWindow.Items.Clear();
                WindowInfo last = new WindowInfo();
                last.Title = LastActiveText();
                testWindow.Items.Add(last);
                testWindow.SelectedIndex = 0;
                foreach (WindowInfo window in engine.TestWindows())
                {
                    testWindow.Items.Add(window);
                    if (chosen != null && chosen.Handle != IntPtr.Zero && chosen.Handle == window.Handle)
                    {
                        testWindow.SelectedItem = window;
                    }
                }
            }
            finally
            {
                testWindow.EndUpdate();
            }
        }

        private void Apply(KeyAction action)
        {
            WindowInfo chosen = testWindow.SelectedItem as WindowInfo;
            engine.ApplyInEditor(action, chosen == null ? IntPtr.Zero : chosen.Handle);
        }

        /// Reflects the engine's state. Called on the user interface thread.
        public void UpdateState()
        {
            if (IsDisposed) return;
            bool active = engine.Running;
            badge.Text = active ? "ACTIVE" : "PAUSED";
            badge.BackColor = active ? Color.FromArgb(222, 244, 228) : Color.FromArgb(234, 234, 238);
            badge.ForeColor = active ? Color.FromArgb(22, 118, 50) : Color.FromArgb(92, 92, 100);
            pause.Text = engine.Enabled ? "Pause" : "Resume";
            UpdateKeyTexts();
            status.Text = engine.Status;
            shortcutStatus.Text = engine.ShortcutStatus;
            currentLayout.Text = engine.CurrentLayoutName;
            if (!testWindow.DroppedDown && testWindow.Items.Count > 0)
            {
                WindowInfo last = (WindowInfo)testWindow.Items[0];
                string text = LastActiveText();
                if (last.Title != text)
                {
                    last.Title = text;
                    int selected = testWindow.SelectedIndex;
                    testWindow.Items[0] = last; // Makes the box re-read the text.
                    testWindow.SelectedIndex = selected;
                }
            }
        }
    }
}
