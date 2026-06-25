using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Toolbox.Library.Forms;
using Toolbox.Library;

namespace FirstPlugin.Forms
{
    public partial class Color8KeySlider : STPanel, IColorPanelCommon
    {
        public bool IsAlpha { get; set; }

        private int SelectedIndex = 0;
        public int Row { get { return row; } }
        public int KeyCount { get { return keyCount; } }

        public event EventHandler ColorSelected;
        //Raised (deferred) after a structural edit (add/remove key) so the editor can refresh the "(N Keys)"
        //label and, if the track dropped to 0 keys, switch it to the Constant panel.
        public event EventHandler KeysEdited;
        //Raised continuously while a key is being dragged, so the editor can update the Time box live.
        public event EventHandler TimeChanged;

        public Color GetColor()
        {
            return (SelectedIndex >= 0 && SelectedIndex < Keys.Count) ? Keys[SelectedIndex].Color : Color.White;
        }

        public float GetTime()
        {
            return (SelectedIndex >= 0 && SelectedIndex < Keys.Count) ? Keys[SelectedIndex].STColor.Time : 0f;
        }

        public void SetColor(Color color)
        {
            if (SelectedIndex >= 0 && SelectedIndex < Keys.Count)
            {
                Keys[SelectedIndex].STColor.Color = color;
                this.Invalidate();
            }
        }

        //Precise time entry for the selected key (driven by the Time box); re-sorts and keeps the key selected.
        public void SetSelectedKeyTime(float time)
        {
            if (emitter == null || SelectedIndex < 0 || SelectedIndex >= keyCount) return;
            SelectedIndex = emitter.SetColorKeyTime(row, SelectedIndex, time);
            ReloadKeys();
            Invalidate();
            if (ColorSelected != null) ColorSelected(this, null);
        }

        public void SelectPanel() { }
        public void DeselectPanel() { }

        public Color8KeySlider()
        {
            InitializeComponent();

            this.SetStyle(
                  ControlStyles.AllPaintingInWmPaint |
                  ControlStyles.UserPaint |
                  ControlStyles.DoubleBuffer |
                  ControlStyles.StandardClick |
                  ControlStyles.StandardDoubleClick |
                  ControlStyles.Selectable,   //so the panel can take focus and receive Delete
                  true);
            TabStop = true;

            BorderStyle = BorderStyle.FixedSingle;
            Paint += panel_Paint;
            MouseDown += Color8KeySlider_MouseDown;
            MouseMove += Color8KeySlider_MouseMove;
            MouseUp += Color8KeySlider_MouseUp;
            MouseDoubleClick += Color8KeySlider_MouseDoubleClick;
            KeyDown += Color8KeySlider_KeyDown;
        }

        private List<KeyFrame> Keys = new List<KeyFrame>();
        private STColor[] keyArray;
        private PTCL.Emitter emitter;
        private int row;
        private int keyCount;
        private bool dragging;

        public void LoadColors(STColor[] keys, int keyCount, PTCL.Emitter emitter, int row)
        {
            this.keyArray = keys;
            this.keyCount = keyCount;
            this.emitter = emitter;
            this.row = row;
            if (SelectedIndex >= keyCount) SelectedIndex = Math.Max(0, keyCount - 1);
            ReloadKeys();
        }

        //Rebuild the visual keyframe list from the backing array (the array IS the emitter's track data, so
        //add/remove/move on it persist via Write's overlay).
        private void ReloadKeys()
        {
            Keys.Clear();
            int n = Math.Min(keyCount, keyArray != null ? keyArray.Length : 0);
            for (int i = 0; i < n; i++)
                Keys.Add(new KeyFrame(keyArray[i]));
            if (SelectedIndex >= 0 && SelectedIndex < Keys.Count)
                Keys[SelectedIndex].IsSelected = true;
        }

        private Point MouseCursor;
        private void panel_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;

            Color firstColor = Color.White;
            Color lastColor = Color.White;
            if (Keys.Count > 0)
            {
                firstColor = Keys[0].Color;
                lastColor = Keys[Keys.Count - 1].Color;
            }

            float keyMarigin = 10f;

            RectangleF r = new RectangleF(ClientRectangle.X, ClientRectangle.Y + keyMarigin, ClientRectangle.Width, ClientRectangle.Height - keyMarigin);

            //Start our gradient brush
            LinearGradientBrush br = new LinearGradientBrush(r, firstColor, lastColor, 0, true);

            List<Color> colors = new List<Color>();
            List<float> frames = new List<float>();

            frames.Add(0);
            colors.Add(firstColor);

            for (int i = 0; i < Keys.Count; i++)
            {
                var currentKey = Keys[i];
                Color c2 = currentKey.Color;
                c2 = Color.FromArgb(c2.R, c2.G, c2.B);

                float p2 = currentKey.STColor.Time;
                p2 = Math.Max(0f, Math.Min(1f, p2));

                colors.Add(c2);
                frames.Add(p2);
            }
            colors.Add(lastColor);
            frames.Add(1);

            //Free key placement can produce equal/non-increasing positions; GDI+ requires strictly increasing
            //ones, so nudge them and fall back to the plain gradient if the blend is still rejected.
            for (int i = 1; i < frames.Count; i++)
                if (frames[i] <= frames[i - 1]) frames[i] = Math.Min(1f, frames[i - 1] + 0.0001f);

            try
            {
                ColorBlend cb = new ColorBlend();
                cb.Positions = frames.ToArray();
                cb.Colors = colors.ToArray();
                br.InterpolationColors = cb;
            }
            catch { /* keep the simple firstColor -> lastColor gradient */ }

            // paint gradient
            g.FillRectangle(br, r);

            for (int i = 0; i < Keys.Count; i++)
            {
                //Create a box to reperesent a key frame
                int keyPos = (int)(ClientRectangle.Width * Keys[i].STColor.Time);
                if (i == Keys.Count - 1 && Keys[i].STColor.Time >= 0.09f)
                    keyPos -= 8;

                Rectangle keyBox = new Rectangle(keyPos, (int)r.Y, 7, (int)r.Height);
                Keys[i].DrawnRectangle = keyBox;

                // paint keys
                Color cursorColor = Color.White;
                if (Keys[i].IsHit(MouseCursor.X, MouseCursor.Y) || Keys[i].IsSelected) {
                    cursorColor = Color.Yellow;
                }

                using (Pen pen = new Pen(Color.Black,1))
                    g.DrawRectangle(pen, keyBox);

                keyBox.Y += 1;
                keyBox.X += 1;

                keyBox.Height -= 2;
                keyBox.Width -= 2;

                using (Pen pen = new Pen(cursorColor, 1))
                    g.DrawRectangle(pen, keyBox);

                keyBox.Y += 1;
                keyBox.X += 1;

                keyBox.Height -= 2;
                keyBox.Width -= 2;

                using (Pen pen = new Pen(Color.Black, 1))
                    g.DrawRectangle(pen, keyBox);


                //Draw key pointer at top

                int keyTopPos = (int)(r.Y - 10);
                keyPos += keyBox.Width;
                Point[] triPoints = { new Point(keyPos - 5, keyTopPos), new Point(keyPos, keyTopPos + 10), new Point(keyPos + 5, keyTopPos) };
                e.Graphics.FillPolygon(new SolidBrush(cursorColor), triPoints);
                e.Graphics.DrawPolygon(new Pen(Color.Black,0.5f), triPoints);
            }

            frames.Clear();
            colors.Clear();
        }

        //Index of the key under a point (with a few px of slack on X), or -1.
        private int HitTest(Point p)
        {
            for (int i = 0; i < Keys.Count; i++)
                if (Keys[i].IsHit(p.X, p.Y)) return i;
            for (int i = 0; i < Keys.Count; i++)
            {
                var rr = Keys[i].DrawnRectangle;
                if (p.X >= rr.X - 3 && p.X <= rr.X + rr.Width + 3) return i;
            }
            return -1;
        }

        private void Color8KeySlider_MouseDown(object sender, MouseEventArgs e)
        {
            MouseCursor = e.Location;
            int hit = HitTest(e.Location);

            if (e.Button == MouseButtons.Right)
            {
                dragging = false;   //a right-click ends any in-progress drag before the menu opens
                if (hit >= 0) ShowKeyMenu(hit, e.Location);
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                if (hit >= 0) { Select(hit); dragging = true; }   //select + start dragging this key
                else dragging = false;                            //empty space: add is double-click
            }
        }

        private void Color8KeySlider_MouseMove(object sender, MouseEventArgs e)
        {
            MouseCursor = e.Location;
            if (dragging && e.Button == MouseButtons.Left && emitter != null && Width > 0)
            {
                float t = (float)e.X / Width;                     //drag horizontally to retime (freely 0..1)
                SelectedIndex = emitter.SetColorKeyTime(row, SelectedIndex, t);
                ReloadKeys();
                if (TimeChanged != null) TimeChanged(this, null); //update the Time box live while dragging
            }
            Invalidate();
        }

        private void Color8KeySlider_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Focus(); //focus the slider so Delete removes the selected key
            if (dragging)
            {
                dragging = false;
                if (ColorSelected != null) ColorSelected(this, null); //refresh picker + Time box for final position
            }
        }

        //Ensure Delete is delivered to KeyDown (rather than swallowed as a navigation key) while focused.
        protected override bool IsInputKey(System.Windows.Forms.Keys keyData)
        {
            if (keyData == System.Windows.Forms.Keys.Delete) return true;
            return base.IsInputKey(keyData);
        }

        private void Color8KeySlider_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || emitter == null || Width <= 0) return;
            if (HitTest(e.Location) >= 0) return;                 //double-click on a key = no-op; on empty = add
            int idx = emitter.AddColorKey(row, (float)e.X / Width);
            if (idx < 0) return;                                  //track full (8 keys)
            keyCount = (int)emitter.GetColorKeyCount(row);
            SelectedIndex = idx;
            ReloadKeys();
            Invalidate();
            if (ColorSelected != null) ColorSelected(this, null);
            RaiseKeysEdited();
        }

        private void Color8KeySlider_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == System.Windows.Forms.Keys.Delete && SelectedIndex >= 0 && SelectedIndex < keyCount)
            {
                RemoveKeyAt(SelectedIndex);
                e.Handled = true;
            }
        }

        private void ShowKeyMenu(int index, Point at)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Remove Key", null, (s, ev) => RemoveKeyAt(index));
            menu.Show(this, at);
        }

        private void RemoveKeyAt(int index)
        {
            if (emitter == null || !emitter.RemoveColorKey(row, index)) return;
            keyCount = (int)emitter.GetColorKeyCount(row);
            if (SelectedIndex >= keyCount) SelectedIndex = Math.Max(0, keyCount - 1);
            ReloadKeys();
            Invalidate();
            RaiseKeysEdited();
        }

        private void Select(int index)
        {
            for (int i = 0; i < Keys.Count; i++) Keys[i].IsSelected = false;
            SelectedIndex = index;
            if (index >= 0 && index < Keys.Count) Keys[index].IsSelected = true;
            Invalidate();
            if (ColorSelected != null) ColorSelected(this, null);
        }

        //Defer so this control finishes its handler before the editor possibly recreates the panel (count -> 0).
        private void RaiseKeysEdited()
        {
            if (KeysEdited == null) return;
            BeginInvoke((MethodInvoker)(() => { var h = KeysEdited; if (h != null) h(this, EventArgs.Empty); }));
        }

        private class KeyFrame
        {
            public bool IsSelected = false;

            public Color Color => STColor.Color;

            public STColor STColor;

            public Rectangle DrawnRectangle;

            public KeyFrame(STColor color)
            {
                STColor = color;
            }

            public bool IsHit(int X, int Y)
            {
                if (DrawnRectangle == null) return false;

                if ((X > DrawnRectangle.X) && (X < DrawnRectangle.X + DrawnRectangle.Width) &&
                    (Y > DrawnRectangle.Y) && (Y < DrawnRectangle.Y + DrawnRectangle.Height))
                    return true;
                else
                    return false;
            }
        }
    }
}
