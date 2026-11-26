using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KeyFlip.Tests
{
    /// An editor that draws its own text and exposes none of it to UI Automation, like
    /// a canvas-based editor: only typing, Backspace, Ctrl+Insert and Shift+Insert work.
    internal sealed class CanvasEditor : Control
    {
        public string Value = "";
        public int SelectionStart;
        public int SelectionLength;
        /// Code editors copy the current line when nothing is selected.
        public bool CopiesLineWhenNothingSelected;
        /// Every key press and click that arrived, with its modifiers: "Ctrl+Shift+Tab".
        private readonly List<string> received = new List<string>();

        /// What arrived since the last call. Safe on any thread.
        public string TakeReceived()
        {
            lock (received)
            {
                string text = string.Join(" ", received);
                received.Clear();
                return text;
            }
        }

        private void Note(string what)
        {
            string text = "";
            if ((ModifierKeys & Keys.Control) != 0) text += "Ctrl+";
            if ((ModifierKeys & Keys.Alt) != 0) text += "Alt+";
            if ((ModifierKeys & Keys.Shift) != 0) text += "Shift+";
            lock (received) received.Add(text + what);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Note("Click");
            base.OnMouseDown(e);
        }

        public CanvasEditor()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            TabStop = true;
            BackColor = Color.White;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return true;
        }

        protected override bool IsInputChar(char character)
        {
            return true;
        }

        private void Replace(string text)
        {
            Value = Value.Substring(0, SelectionStart) + text + Value.Substring(SelectionStart + SelectionLength);
            SelectionStart += text.Length;
            SelectionLength = 0;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyValue)
            {
                case (int)Keys.ShiftKey:
                case (int)Keys.ControlKey:
                case (int)Keys.Menu:
                case (int)Keys.LWin:
                case (int)Keys.RWin:
                case 0xE8: // The key KeyFlip presses to keep menus closed.
                    break;
                default:
                    Note(e.KeyCode.ToString());
                    break;
            }
            if (e.KeyCode == Keys.Insert && e.Control && !e.Shift && !e.Alt)
            {
                string copied = SelectionLength > 0 ? Value.Substring(SelectionStart, SelectionLength)
                    : CopiesLineWhenNothingSelected ? Value + "\r\n" : null;
                if (copied != null)
                {
                    // Marked like KeyFlip's own temporary text, to keep the check out of clipboard history.
                    WinClipboard.SetRaw(new[]
                    {
                        new KeyValuePair<uint, byte[]>(Native.CF_UNICODETEXT, Encoding.Unicode.GetBytes(copied + "\0")),
                        new KeyValuePair<uint, byte[]>(
                            Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), new byte[4]),
                        new KeyValuePair<uint, byte[]>(Native.RegisterClipboardFormat("CanIncludeInClipboardHistory"), new byte[4])
                    });
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Insert && e.Shift && !e.Control && !e.Alt)
            {
                string pasted = new WinClipboard().GetText();
                if (pasted != null) Replace(pasted);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (e.KeyChar == '\b')
            {
                if (SelectionLength == 0 && SelectionStart > 0)
                {
                    SelectionStart--;
                    SelectionLength = 1;
                }
                Replace("");
            }
            else if (!char.IsControl(e.KeyChar))
            {
                Replace(e.KeyChar.ToString());
            }
            e.Handled = true;
            base.OnKeyPress(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, Value, Font, new Point(4, 4), Color.Black, TextFormatFlags.NoPrefix);
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, Focused ? Color.DodgerBlue : Color.Silver, ButtonBorderStyle.Solid);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }
    }

    /// A window with ordinary edit controls on its own user interface thread, for the
    /// desktop checks. In quiet mode it is off screen and can never take the focus.
    internal sealed class EditorHost : IDisposable
    {
        private sealed class HostForm : Form
        {
            public bool Quiet;
            /// Shown behind the window in front, as an ordinary window.
            public bool Behind;

            protected override bool ShowWithoutActivation
            {
                get { return Quiet || Behind; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                    if (Quiet) parameters.ExStyle |= 0x08000000 | 0x00000080;
                    return parameters;
                }
            }
        }

        public TextBox Plain;
        public TextBox Multiline;
        public RichTextBox Rich;
        public TextBox Password;
        public TextBox ReadOnly;
        public Button Button;
        public CanvasEditor Canvas;
        public IntPtr Handle;
        public uint ThreadId;

        private readonly Thread thread;
        private HostForm form;
        private HostForm second;
        private Exception failure;

        public EditorHost(bool quiet, string title)
        {
            using (ManualResetEventSlim ready = new ManualResetEventSlim(false))
            {
                thread = new Thread(delegate() { Run(quiet, title, ready); });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Name = "Editor host";
                thread.Start();
                ready.Wait();
            }
            if (failure != null) throw new InvalidOperationException("Could not create the test window.", failure);
        }

        private void Run(bool quiet, string title, ManualResetEventSlim ready)
        {
            try
            {
                Application.EnableVisualStyles();
                form = new HostForm();
                form.Quiet = quiet;
                form.Text = title;
                form.ClientSize = new Size(460, 330);
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = !quiet;
                form.TopMost = !quiet;
                form.StartPosition = quiet ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
                if (quiet) form.Location = new Point(-30000, -30000);
                // A classic menu bar: a lone Alt tap would normally activate it.
                if (!quiet)
                {
                    form.Menu = new MainMenu(new[] { new MenuItem("&File", new[] { new MenuItem("&Close") }) });
                }

                Plain = new TextBox();
                Plain.Bounds = new Rectangle(12, 12, 436, 23);
                Multiline = new TextBox();
                Multiline.Multiline = true;
                Multiline.AcceptsReturn = true;
                Multiline.Bounds = new Rectangle(12, 44, 436, 60);
                Rich = new RichTextBox();
                Rich.Bounds = new Rectangle(12, 114, 436, 60);
                Password = new TextBox();
                Password.UseSystemPasswordChar = true;
                Password.Bounds = new Rectangle(12, 184, 436, 23);
                ReadOnly = new TextBox();
                ReadOnly.ReadOnly = true;
                ReadOnly.Text = "read only";
                ReadOnly.Bounds = new Rectangle(12, 216, 436, 23);
                Button = new Button();
                Button.Text = "Button";
                Button.Bounds = new Rectangle(12, 250, 90, 27);
                Canvas = new CanvasEditor();
                Canvas.Bounds = new Rectangle(12, 288, 436, 27);
                form.Controls.AddRange(new Control[] { Plain, Multiline, Rich, Password, ReadOnly, Button, Canvas });

                form.Shown += delegate
                {
                    Handle = form.Handle;
                    ThreadId = Native.GetCurrentThreadId();
                    ready.Set();
                };
                Application.Run(form);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                if (!ready.IsSet) ready.Set();
            }
        }

        public void Do(Action action)
        {
            form.Invoke(action);
        }

        /// Another window of the same program, behind the first; it can be minimized.
        public IntPtr OpenSecond(string title)
        {
            return Get(delegate
            {
                second = new HostForm();
                second.Behind = true;
                second.Text = title;
                second.ClientSize = new Size(300, 120);
                second.FormBorderStyle = FormBorderStyle.FixedDialog;
                second.MaximizeBox = false;
                second.StartPosition = FormStartPosition.Manual;
                second.Location = new Point(form.Left + 40, form.Top + 40);
                second.Show();
                // Above other programs, like the first window. The framework would
                // activate a window that is shown with TopMost set.
                Native.SetWindowPos(second.Handle, (IntPtr)(-1), 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                return second.Handle;
            });
        }

        public void CloseSecond()
        {
            Do(delegate
            {
                if (second != null) second.Close();
                second = null;
            });
        }

        /// The middle of a control, in screen coordinates.
        public Point CenterOf(Control control)
        {
            return Get(delegate { return control.PointToScreen(new Point(control.Width / 2, control.Height / 2)); });
        }

        public T Get<T>(Func<T> read)
        {
            return (T)form.Invoke(read);
        }

        public IntPtr HandleOf(Control control)
        {
            return Get(delegate { return control.Handle; });
        }

        /// Replaces a control's text and selection the way a program would, not a user.
        public void Set(TextBoxBase box, string text, int selectionStart, int selectionLength)
        {
            Do(delegate
            {
                box.Text = text;
                box.Select(selectionStart, selectionLength);
            });
        }

        public string TextOf(TextBoxBase box)
        {
            return Get(delegate { return box.Text; });
        }

        public int SelectionStartOf(TextBoxBase box)
        {
            return Get(delegate { return box.SelectionStart; });
        }

        public int SelectionLengthOf(TextBoxBase box)
        {
            return Get(delegate { return box.SelectionLength; });
        }

        public void Dispose()
        {
            try
            {
                if (form != null && form.IsHandleCreated) form.BeginInvoke(new Action(form.Close));
            }
            catch (InvalidOperationException)
            {
            }
            thread.Join(3000);
        }
    }
}
