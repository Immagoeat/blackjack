using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using StbTrueTypeSharp;

namespace BlackjackSimulator
{
    // ═══════════════════════════════════════════════════════════
    //  FONT
    // ═══════════════════════════════════════════════════════════
    public unsafe class Font : IDisposable
    {
        private readonly GL _gl;
        private readonly uint _tex;
        private readonly StbTrueType.stbtt_bakedchar[] _cd;
        private const int FIRST = 32, COUNT = 96;
        private const float BAKE_SZ = 32f;
        private const int AW = 512, AH = 512;

        public readonly float Ascent;
        public readonly float LineHeight;

        public Font(GL gl, string path)
        {
            _gl = gl;
            byte[] ttf   = File.ReadAllBytes(path);
            byte[] atlas = new byte[AW * AH];
            _cd = new StbTrueType.stbtt_bakedchar[COUNT];

            fixed (byte* pb = ttf) fixed (byte* pa = atlas) fixed (StbTrueType.stbtt_bakedchar* pc = _cd)
                StbTrueType.stbtt_BakeFontBitmap(pb, 0, BAKE_SZ, pa, AW, AH, FIRST, COUNT, pc);

            var A  = _cd['A' - FIRST];
            Ascent     = -A.yoff;
            LineHeight = BAKE_SZ * 1.25f;

            _tex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _tex);
            fixed (byte* pa2 = atlas)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8,
                    AW, AH, 0, PixelFormat.Red, PixelType.UnsignedByte, pa2);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        public uint Texture => _tex;

        public float CapHeight(float sz) => Ascent * (sz / BAKE_SZ);

        public float Measure(string s, float sz = 18f)
        {
            float w = 0, sc = sz / BAKE_SZ;
            foreach (char ch in s)
            {
                if (ch < FIRST || ch >= FIRST + COUNT) { w += 8 * sc; continue; }
                w += _cd[ch - FIRST].xadvance * sc;
            }
            return w;
        }

        public (float x0,float y0,float x1,float y1, float u0,float v0,float u1,float v1)
            GetQuad(char ch, ref float cx, float topY, float sz)
        {
            if (ch < FIRST || ch >= FIRST + COUNT)
            { cx += 8 * (sz / BAKE_SZ); return (0,0,0,0,0,0,0,0); }

            float sc       = sz / BAKE_SZ;
            float baseline = topY + Ascent * sc;
            var   bd       = _cd[ch - FIRST];
            float x0 = cx + bd.xoff * sc;
            float y0 = baseline + bd.yoff * sc;
            float x1 = x0 + (bd.x1 - bd.x0) * sc;
            float y1 = y0 + (bd.y1 - bd.y0) * sc;
            cx += bd.xadvance * sc;
            return (x0,y0,x1,y1, bd.x0/(float)AW, bd.y0/(float)AH, bd.x1/(float)AW, bd.y1/(float)AH);
        }

        public void Dispose() { _gl.DeleteTexture(_tex); }
    }

    // ═══════════════════════════════════════════════════════════
    //  RENDERER
    // ═══════════════════════════════════════════════════════════
    public unsafe class Renderer : IDisposable
    {
        private readonly GL   _gl;
        private readonly uint _vao, _vbo, _prog;
        private readonly Font _font;
        private int  _W, _H;
        private readonly List<float> _buf = new(1 << 16);
        private bool _texMode = false;

        const string VERT = @"#version 330 core
layout(location=0) in vec2 aPos;
layout(location=1) in vec2 aUV;
layout(location=2) in vec4 aCol;
out vec2 vUV; out vec4 vCol;
uniform mat4 uP;
void main(){ gl_Position = uP * vec4(aPos,0,1); vUV=aUV; vCol=aCol; }";

        const string FRAG = @"#version 330 core
in vec2 vUV; in vec4 vCol;
out vec4 frag;
uniform sampler2D uTex;
uniform bool uTxt;
void main(){
    if(uTxt){ float a=texture(uTex,vUV).r; frag=vec4(vCol.rgb,vCol.a*a); }
    else     { frag=vCol; }
}";

        public Renderer(GL gl, Font font, int w, int h)
        {
            _gl = gl; _font = font; _W = w; _H = h;

            uint vs = Compile(ShaderType.VertexShader, VERT);
            uint fs = Compile(ShaderType.FragmentShader, FRAG);
            _prog = gl.CreateProgram();
            gl.AttachShader(_prog, vs); gl.AttachShader(_prog, fs);
            gl.LinkProgram(_prog);
            gl.DeleteShader(vs); gl.DeleteShader(fs);

            _vao = gl.GenVertexArray();
            _vbo = gl.GenBuffer();
            gl.BindVertexArray(_vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, 256*1024, null, BufferUsageARB.DynamicDraw);
            int stride = 8 * sizeof(float);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)0);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, (uint)stride, (void*)(2*sizeof(float)));
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, (uint)stride, (void*)(4*sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.BindVertexArray(0);
        }

        private uint Compile(ShaderType t, string src)
        {
            uint s = _gl.CreateShader(t);
            _gl.ShaderSource(s, src); _gl.CompileShader(s);
            _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new Exception(_gl.GetShaderInfoLog(s));
            return s;
        }

        public void Resize(int w, int h) { _W = w; _H = h; }

        public void Rect(float x, float y, float w, float h, Vector4 c)
        {
            SwitchMode(false);
            Quad(x,y, x+w,y, x+w,y+h, x,y+h, 0,0,0,0,0,0,0,0, c);
        }

        public float Text(string s, float x, float y, Vector4 col, float sz = 18f)
        {
            SwitchMode(true);
            float cx = x;
            foreach (char ch in s)
            {
                var (x0,y0,x1,y1,u0,v0,u1,v1) = _font.GetQuad(ch, ref cx, y, sz);
                if (x1 == 0 && y1 == 0) continue;
                Quad(x0,y0, x1,y0, x1,y1, x0,y1, u0,v0, u1,v0, u1,v1, u0,v1, col);
            }
            return cx;
        }

        public float Measure(string s, float sz = 18f)   => _font.Measure(s, sz);
        public float CapHeight(float sz)                  => _font.CapHeight(sz);

        public void TextC(string s, float bx, float by, float bw, float bh, Vector4 col, float sz = 18f)
        {
            float tw = Measure(s, sz), th = CapHeight(sz);
            Text(s, bx + (bw - tw) * 0.5f, by + (bh - th) * 0.5f, col, sz);
        }

        public void TextR(string s, float x, float y, Vector4 col, float sz = 18f)
            => Text(s, x - Measure(s, sz), y, col, sz);

        public void Begin() { _buf.Clear(); _texMode = false; }
        public void End()   { Flush(); }

        private void SwitchMode(bool tex) { if (tex != _texMode) Flush(); _texMode = tex; }

        private void Flush()
        {
            if (_buf.Count == 0) return;
            var arr = _buf.ToArray();
            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* p = arr)
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                    (nuint)(arr.Length * sizeof(float)), p);
            _gl.UseProgram(_prog);
            SetProj();
            _gl.Uniform1(_gl.GetUniformLocation(_prog, "uTxt"), _texMode ? 1 : 0);
            _gl.Uniform1(_gl.GetUniformLocation(_prog, "uTex"), 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _texMode ? _font.Texture : 0);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(arr.Length / 8));
            _gl.BindVertexArray(0);
            _buf.Clear();
        }

        private void SetProj()
        {
            var m = Matrix4x4.CreateOrthographicOffCenter(0, _W, _H, 0, -1, 1);
            float[] f = {
                m.M11,m.M12,m.M13,m.M14, m.M21,m.M22,m.M23,m.M24,
                m.M31,m.M32,m.M33,m.M34, m.M41,m.M42,m.M43,m.M44
            };
            _gl.UniformMatrix4(_gl.GetUniformLocation(_prog,"uP"), 1, false, f.AsSpan());
        }

        private void Quad(
            float x0,float y0, float x1,float y1, float x2,float y2, float x3,float y3,
            float u0,float v0, float u1,float v1, float u2,float v2, float u3,float v3,
            Vector4 c)
        {
            void V(float x,float y,float u,float v)
            { _buf.Add(x);_buf.Add(y);_buf.Add(u);_buf.Add(v);
              _buf.Add(c.X);_buf.Add(c.Y);_buf.Add(c.Z);_buf.Add(c.W); }
            V(x0,y0,u0,v0); V(x1,y1,u1,v1); V(x2,y2,u2,v2);
            V(x0,y0,u0,v0); V(x2,y2,u2,v2); V(x3,y3,u3,v3);
        }

        public void Dispose()
        {
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteProgram(_prog);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  PALETTE
    // ═══════════════════════════════════════════════════════════
    static class P
    {
        public static Vector4 FeltGreen      => V(0.09f, 0.38f, 0.18f);
        public static Vector4 FeltDark       => V(0.07f, 0.28f, 0.13f);
        public static Vector4 FeltLight      => V(0.12f, 0.46f, 0.22f);
        public static Vector4 Wood           => V(0.28f, 0.16f, 0.06f);
        public static Vector4 WoodLight      => V(0.42f, 0.26f, 0.10f);
        public static Vector4 WoodDark       => V(0.18f, 0.10f, 0.04f);
        public static Vector4 Bg             => V(0.10f, 0.07f, 0.04f);
        public static Vector4 Panel          => V(0.14f, 0.10f, 0.06f);
        public static Vector4 PanelDark      => V(0.10f, 0.07f, 0.04f);
        public static Vector4 SidePanel      => V(0.12f, 0.08f, 0.05f);
        public static Vector4 Gold           => V(1.00f, 0.82f, 0.20f);
        public static Vector4 GoldDim        => V(0.55f, 0.40f, 0.05f);
        public static Vector4 GoldBright     => V(1.00f, 0.92f, 0.50f);
        public static Vector4 Red            => V(0.90f, 0.15f, 0.20f);
        public static Vector4 RedDim         => V(0.50f, 0.08f, 0.12f);
        public static Vector4 Green          => V(0.20f, 0.82f, 0.38f);
        public static Vector4 GreenDim       => V(0.08f, 0.38f, 0.15f);
        public static Vector4 Blue           => V(0.25f, 0.55f, 1.00f);
        public static Vector4 Purple         => V(0.55f, 0.22f, 0.85f);
        public static Vector4 White          => V(0.97f, 0.95f, 0.90f);
        public static Vector4 Muted          => V(0.60f, 0.55f, 0.45f);
        public static Vector4 Dim            => V(0.35f, 0.30f, 0.22f);
        public static Vector4 Black          => V(0.00f, 0.00f, 0.00f);
        public static Vector4 CardFace       => V(0.98f, 0.97f, 0.93f);
        public static Vector4 CardBack       => V(0.12f, 0.22f, 0.55f);
        public static Vector4 CardBackStripe => V(0.18f, 0.30f, 0.70f);
        public static Vector4 CardBorder     => V(0.50f, 0.45f, 0.35f);
        public static Vector4 CardBlack      => V(0.08f, 0.07f, 0.06f);
        public static Vector4 CardRed        => V(0.88f, 0.12f, 0.18f);
        public static Vector4 ChipRed        => V(0.75f, 0.10f, 0.10f);
        public static Vector4 ChipBlue       => V(0.10f, 0.20f, 0.70f);
        public static Vector4 ChipGreen      => V(0.10f, 0.50f, 0.18f);
        public static Vector4 ChipBlack      => V(0.15f, 0.13f, 0.12f);
        public static Vector4 ChipPurple     => V(0.42f, 0.10f, 0.55f);
        public static Vector4 ChipOrange     => V(0.80f, 0.40f, 0.05f);
        private static Vector4 V(float r, float g, float b, float a = 1f) => new(r, g, b, a);
        public static Vector4 A(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);
        public static Vector4 Lerp(Vector4 a, Vector4 b, float t) =>
            new(a.X+(b.X-a.X)*t, a.Y+(b.Y-a.Y)*t, a.Z+(b.Z-a.Z)*t, a.W+(b.W-a.W)*t);
    }

    // ═══════════════════════════════════════════════════════════
    //  HIT RECT
    // ═══════════════════════════════════════════════════════════
    public record HitRect(float X, float Y, float W, float H, Action OnClick)
    {
        public bool Contains(float mx, float my) =>
            mx >= X && mx <= X+W && my >= Y && my <= Y+H;
    }

    // ═══════════════════════════════════════════════════════════
    //  APP
    // ═══════════════════════════════════════════════════════════
    class App
    {
        IWindow     _win   = null!;
        GL          _gl    = null!;
        IInputContext _inp = null!;
        Renderer    _r     = null!;
        Font        _font  = null!;
        GS          _g     = new();
        AudioEngine _audio = new();

        // Per-slot display names (editable in future; default to "Run N")
        readonly string[] _slotNames = { "Run 1", "Run 2", "Run 3" };

        float _mx, _my;
        readonly List<HitRect> _hits = new();

        double _now;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        // Menu hand animation
        double _menuEnterTime = -999;
        Phase  _prevPhase     = Phase.SlotSelect;

        int W => _win.Size.X;
        int H => _win.Size.Y;

        // ── lifecycle ──────────────────────────────────────────
        public void Run()
        {
            var opts = WindowOptions.Default with
            {
                Title = "Blackjack",
                Size  = new Vector2D<int>(1200, 800),
                PreferredDepthBufferBits   = 0,
                PreferredStencilBufferBits = 0,
            };
            _win = Window.Create(opts);
            _win.Load    += OnLoad;
            _win.Render  += _ => OnRender();
            _win.Resize  += s => { _r?.Resize(s.X, s.Y); _gl?.Viewport(0, 0, (uint)s.X, (uint)s.Y); };
            _win.Closing += () =>
            {
                if (_g.Phase != Phase.SlotSelect)
                    SaveData.From(_g, _slotNames[_g.SlotIndex]).Save(_g.SlotIndex);
                _r?.Dispose(); _font?.Dispose(); _inp?.Dispose(); _audio.Dispose();
            };
            _win.Run();
        }

        private void OnLoad()
        {
            _gl  = _win.CreateOpenGL();
            _inp = _win.CreateInput();

            // Load slot names from save files
            for (int s = 0; s < 3; s++)
            {
                var sd = SaveData.Load(s);
                if (SaveData.Exists(s)) _slotNames[s] = sd.SlotName;
            }

            string fontPath = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf";
            if (!File.Exists(fontPath))
                fontPath = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
            _font = new Font(_gl, fontPath);
            _r    = new Renderer(_gl, _font, W, H);

            _audio.Init();

            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            foreach (var kb in _inp.Keyboards)
                kb.KeyDown += (_, k, _) => OnKey(k);
            foreach (var m in _inp.Mice)
            {
                m.MouseMove += (_, pos)  => { _mx = pos.X; _my = pos.Y; };
                m.MouseDown += (_, btn)  => { if (btn == MouseButton.Left) Click(); };
            }
        }

        private void Click()
        {
            foreach (var h in _hits)
                if (h.Contains(_mx, _my)) { _audio.Play("click"); h.OnClick(); return; }
        }

        // ── keyboard ──────────────────────────────────────────
        private void OnKey(Key k)
        {
            switch (_g.Phase)
            {
                case Phase.SlotSelect:
                    if (k == Key.Up)   _g.SlotSel = Math.Max(0, _g.SlotSel - 1);
                    if (k == Key.Down) _g.SlotSel = Math.Min(2, _g.SlotSel + 1);
                    if (k is Key.Enter or Key.Space) LoadSlot(_g.SlotSel);
                    if (k == Key.Q) _win.Close();
                    break;

                case Phase.Menu:
                    if (k == Key.Up)   { if (_g.MenuSel < 0) _g.MenuSel = 0; else _g.MenuSel = Math.Max(0, _g.MenuSel - 1); }
                    if (k == Key.Down) { if (_g.MenuSel < 0) _g.MenuSel = 0; else _g.MenuSel = Math.Min(4, _g.MenuSel + 1); }
                    if (k is Key.Enter or Key.Space && _g.MenuSel >= 0) DoMenuSel(_g.MenuSel);
                    if (k == Key.Q) _win.Close();
                    break;

                case Phase.Betting:
                    if (k == Key.Left)  _g.BetAmount = Math.Max(10,       _g.BetAmount - 10);
                    if (k == Key.Right) _g.BetAmount = Math.Min(_g.Chips, _g.BetAmount + 10);
                    if (k == Key.Down)  _g.BetAmount = Math.Max(10,       _g.BetAmount - 50);
                    if (k == Key.Up)    _g.BetAmount = Math.Min(_g.Chips, _g.BetAmount + 50);
                    if (k is Key.Enter or Key.Space) DoDeal();
                    if (k == Key.Escape) _g.Phase = Phase.Menu;
                    break;

                case Phase.PlayerTurn:
                    var h = CurHand();
                    if (h == null) { DoDealer(); break; }
                    if (k == Key.H) { h.AddCard(DealCard()); if (h.IsBust()) NextHand(); }
                    if (k == Key.S) NextHand();
                    if (k == Key.D && h.Cards.Count == 2 && _g.Chips >= h.Bet) DoDouble(h);
                    if (k == Key.P && h.CanSplit() && _g.Hands.Count < 4 && _g.Chips >= h.Bet) DoSplit(h);
                    if (k == Key.Escape) NextHand();
                    break;

                case Phase.Results:
                    if (k is Key.Enter or Key.Space)
                    {
                        if (_g.AutoPlay) { _g.BetAmount = Math.Min(_g.BetAmount, _g.Chips); _g.Phase = Phase.Betting; }
                        else _g.Phase = Phase.Menu;
                    }
                    if (k == Key.Escape) _g.Phase = Phase.Menu;
                    break;

                case Phase.Stats:
                    if (k is Key.Enter or Key.Space or Key.Escape) _g.Phase = Phase.Menu;
                    break;

                case Phase.BuyChips:
                    if (k == Key.Up)   _g.BuySel = Math.Max(0, _g.BuySel - 1);
                    if (k == Key.Down) _g.BuySel = Math.Min(2, _g.BuySel + 1);
                    if (k is Key.Enter or Key.Space) DoBuy(_g.BuySel);
                    if (k == Key.Escape) _g.Phase = Phase.Menu;
                    break;
            }
        }

        // ── game actions ──────────────────────────────────────
        private void LoadSlot(int slot)
        {
            _g = new GS { SlotIndex = slot, SlotSel = slot };
            var sd = SaveData.Load(slot);
            sd.ApplyTo(_g);
            _slotNames[slot] = sd.SlotName;
            _g.Phase = Phase.Menu;
            _g.MenuSel = -1;
        }

        private void DoMenuSel(int i)
        {
            switch (i)
            {
                case 0: _g.BetAmount = Math.Min(_g.BetAmount, _g.Chips); _g.Phase = Phase.Betting; break;
                case 1: _g.Phase = Phase.BuyChips; _g.BuySel = 0; break;
                case 2: _g.Phase = Phase.Stats; break;
                case 3: _g.Phase = Phase.SlotSelect; break;
                case 4: _win.Close(); break;
            }
        }

        private void DoDeal()
        {
            _g.Chips -= _g.BetAmount;
            _g.Dealer = new Hand();
            _g.Hands  = new List<Hand> { new Hand { Bet = _g.BetAmount } };
            _g.ActiveHand  = 0;
            _g.DealerShown = false;
            _g.Results.Clear();

            double t    = _now;
            double step = 0.18;

            var p0   = _g.Deck.Deal(); p0.DealTime   = t;          p0.IsDealer   = false;
            var d0   = _g.Deck.Deal(); d0.DealTime   = t + step;   d0.IsDealer   = true;
            var p1   = _g.Deck.Deal(); p1.DealTime   = t + step*2; p1.IsDealer   = false;
            var hole = _g.Deck.Deal(); hole.FaceDown = true;
                                       hole.DealTime = t + step*3; hole.IsDealer = true;

            _g.Hands[0].AddCard(p0);
            _g.Dealer.AddCard(d0);
            _g.Hands[0].AddCard(p1);
            _g.Dealer.AddCard(hole);

            for (int i = 0; i < 4; i++)
            {
                int di = i;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep((int)(di * step * 1000));
                    _audio.Play("deal");
                });
            }

            hole.FaceDown = false;
            bool dbj = _g.Dealer.IsBlackjack();
            hole.FaceDown = !dbj;

            if (dbj)
            {
                foreach (var c in _g.Dealer.Cards) c.FaceDown = false;
                _g.DealerShown = true;
                _g.HandsPlayed++;
                if (_g.Hands[0].IsBlackjack())
                { _g.Chips += _g.BetAmount; _g.Results.Add("PUSH  — both Blackjack"); }
                else
                { _g.NetWinnings -= _g.BetAmount; _g.Results.Add("DEALER BLACKJACK  — you lose"); }
                _g.AutoPlay = true;
                _g.Phase = Phase.Results;
                return;
            }

            if (_g.Hands[0].IsBlackjack()) { DoDealer(); return; }
            _g.Phase = Phase.PlayerTurn;
        }

        private void DoDouble(Hand h)
        {
            _g.Chips -= h.Bet;
            h.Bet *= 2;
            h.IsDoubledDown = true;
            h.AddCard(DealCard());
            NextHand();
        }

        private void DoSplit(Hand h)
        {
            _g.Chips -= h.Bet;
            var nh = new Hand { Bet = h.Bet, IsSplit = true };
            h.IsSplit = true;
            nh.AddCard(h.Cards[1]);
            h.Cards.RemoveAt(1);
            h.AddCard(DealCard());
            nh.AddCard(DealCard());
            _g.Hands.Insert(_g.ActiveHand + 1, nh);
        }

        private void NextHand()
        {
            _g.ActiveHand++;
            if (_g.ActiveHand >= _g.Hands.Count) DoDealer();
        }

        private void DoDealer()
        {
            foreach (var c in _g.Dealer.Cards) c.FaceDown = false;
            _g.DealerShown = true;
            bool allBust = _g.Hands.All(h => h.IsBust());
            if (!allBust)
                while (DealerHits(_g.Dealer))
                    _g.Dealer.AddCard(DealCard(true));
            Settle();
            _g.AutoPlay = true;
            _g.Phase = Phase.Results;
        }

        private bool DealerHits(Hand d)
        {
            int s = d.Score();
            return s < 17 || (s == 17 && d.IsSoftHand());
        }

        private void Settle()
        {
            _g.Results.Clear();
            int  ds = _g.Dealer.Score();
            bool db = _g.Dealer.IsBust();
            string resultSound = "lose";

            for (int i = 0; i < _g.Hands.Count; i++)
            {
                var h   = _g.Hands[i];
                int bet = h.Bet;
                string pfx = _g.Hands.Count > 1 ? $"H{i+1}: " : "";
                _g.HandsPlayed++;

                if (h.IsBust())
                { _g.Results.Add($"{pfx}BUST  -${bet}"); _g.NetWinnings -= bet; resultSound = "bust"; continue; }

                if (h.IsBlackjack() && !_g.Dealer.IsBlackjack())
                {
                    int pay = (int)(bet * 1.5);
                    _g.Chips += bet + pay;
                    _g.Results.Add($"{pfx}BLACKJACK  +${pay}");
                    _g.HandsWon++; _g.NetWinnings += pay; resultSound = "blackjack"; continue;
                }

                int ps = h.Score();
                if (db || ps > ds)
                {
                    _g.Chips += bet * 2;
                    _g.Results.Add($"{pfx}WIN  +${bet}  ({ps} vs {ds})");
                    _g.HandsWon++; _g.NetWinnings += bet;
                    if (resultSound != "blackjack") resultSound = "win";
                    continue;
                }
                if (ps == ds)
                {
                    _g.Chips += bet;
                    _g.Results.Add($"{pfx}PUSH  (both {ps})");
                    if (resultSound == "lose") resultSound = "push";
                    continue;
                }
                _g.Results.Add($"{pfx}LOSE  -${bet}  ({ps} vs {ds})");
                _g.NetWinnings -= bet;
            }

            SaveData.From(_g, _slotNames[_g.SlotIndex]).Save(_g.SlotIndex);
            string snd = resultSound;
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(400); _audio.Play(snd); });
        }

        private Card DealCard(bool isDealer = false)
        {
            var c = _g.Deck.Deal();
            c.DealTime = _now;
            c.IsDealer = isDealer;
            _audio.Play("deal");
            return c;
        }

        private void DoBuy(int sel)
        {
            int[] amt = { 500, 1000, 5000 };
            _g.Chips += amt[sel];
            _g.Phase = Phase.Menu;
            SaveData.From(_g, _slotNames[_g.SlotIndex]).Save(_g.SlotIndex);
        }

        private Hand? CurHand()
        {
            if (_g.ActiveHand >= _g.Hands.Count) return null;
            return _g.Hands[_g.ActiveHand];
        }

        // ── layout constants ──────────────────────────────────
        const float HUD_H  = 44f;
        const float RAIL_H = 22f;
        const float ACT_H  = 96f;
        const float SIDE_W = 280f;

        // ── render ────────────────────────────────────────────
        private void OnRender()
        {
            _now = _clock.Elapsed.TotalSeconds;
            _gl.ClearColor(P.Bg.X, P.Bg.Y, P.Bg.Z, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);
            _r.Begin();
            _hits.Clear();

            DrawTableBg();

            switch (_g.Phase)
            {
                case Phase.SlotSelect: DrawSlotSelect(); break;
                case Phase.Menu:       DrawMenu();        break;
                case Phase.Betting:
                    DrawBettingOverlay();
                    DrawBettingSidePanel();
                    break;
                case Phase.PlayerTurn:
                    DrawCards();
                    DrawActionBar();
                    break;
                case Phase.Results:
                    DrawCards();
                    DrawResultsBar();
                    break;
                case Phase.BuyChips: DrawBuyChips(); break;
                case Phase.Stats:    DrawStats();    break;
            }

            if (_g.Phase != Phase.SlotSelect) DrawHUD();
            _r.End();
        }

        // ── table background ──────────────────────────────────
        private void DrawTableBg()
        {
            _r.Rect(0, 0, W, H, P.Bg);
            float tx = 30, ty = 18, tw = W - 60, th = H - HUD_H - 18;

            _r.Rect(tx,    ty,    tw,    th,    P.FeltDark);
            _r.Rect(tx+10, ty+10, tw-20, th-20, P.FeltGreen);
            _r.Rect(tx+24, ty+24, tw-48, th-48, P.FeltLight);
            _r.Rect(tx+10, ty+10, tw-20, th-20, P.A(P.FeltGreen, 0.55f));

            Border(tx+8,  ty+8,  tw-16, th-16, P.A(P.Gold, 0.35f), 2);
            Border(tx+12, ty+12, tw-24, th-24, P.A(P.Gold, 0.15f), 1);

            float railTop = ty + th - RAIL_H;
            _r.Rect(tx, railTop,         tw, RAIL_H, P.Wood);
            _r.Rect(tx, railTop,         tw, 3,      P.WoodLight);
            _r.Rect(tx, railTop+RAIL_H-3,tw, 3,      P.WoodDark);
            _r.Rect(tx+20, railTop+7, tw-40, 4, P.A(P.Gold, 0.4f));

            string dlbl = "DEALER";
            _r.Text(dlbl, (W - _r.Measure(dlbl, 13)) / 2f, ty + 14, P.A(P.Gold, 0.25f), 13);
            string rule = "BLACKJACK  PAYS  3  TO  2";
            _r.Text(rule, (W - _r.Measure(rule, 13)) / 2f, ty + th - RAIL_H - 28, P.A(P.Gold, 0.22f), 13);
        }

        // ── HUD ───────────────────────────────────────────────
        private void DrawHUD()
        {
            float by = H - HUD_H;
            _r.Rect(0, by, W, HUD_H, P.PanelDark);
            _r.Rect(0, by, W, 2, P.A(P.Gold, 0.5f));
            float ty = by + (HUD_H - _r.CapHeight(17)) / 2f;
            _r.Text($"CHIPS  ${_g.Chips:N0}", 24, ty, P.Gold, 17);
            string slotLbl = $"Slot {_g.SlotIndex + 1}: {_slotNames[_g.SlotIndex]}";
            float slotW = _r.Measure(slotLbl, 13);
            _r.Text(slotLbl, (W - slotW) / 2f, ty + 2, P.A(P.Muted, 0.6f), 13);
            string info = $"Hands {_g.HandsPlayed}   Won {_g.HandsWon}   Net {(_g.NetWinnings >= 0 ? "+" : "")}${_g.NetWinnings:N0}";
            _r.TextR(info, W - 24, ty, P.Muted, 15);
        }

        // ── slot select ───────────────────────────────────────
        private void DrawSlotSelect()
        {
            _r.Rect(0, 0, W, H, P.A(P.Black, 0.72f));

            CentreText("BLACKJACK", H * 0.14f, P.Gold, 52);
            CentreText("Choose a save slot", H * 0.14f + _r.CapHeight(52) + 12, P.A(P.Muted, 0.75f), 16);

            float cw = 300, ch = 130, gap = 24;
            float totalW = 3 * cw + 2 * gap;
            float sx = (W - totalW) / 2f;
            float cy = H / 2f - ch / 2f;

            for (int i = 0; i < 3; i++)
            {
                float bx   = sx + i * (cw + gap);
                bool  sel  = _g.SlotSel == i;
                bool  hov  = _mx >= bx && _mx <= bx + cw && _my >= cy && _my <= cy + ch;
                bool  act  = sel || hov;
                bool  has  = SaveData.Exists(i);

                var accent = has ? P.Gold : P.Muted;
                _r.Rect(bx, cy, cw, ch, act ? P.A(accent, 0.18f) : P.A(P.Black, 0.5f));
                Border(bx, cy, cw, ch, act ? accent : P.A(accent, 0.35f), act ? 3 : 1);

                // Slot number
                _r.TextC($"SLOT {i+1}", bx, cy + 10, cw, _r.CapHeight(14) + 4, act ? P.Gold : P.Muted, 14);

                if (has)
                {
                    var sd = SaveData.Load(i);
                    _r.TextC(_slotNames[i], bx, cy + 36, cw, _r.CapHeight(17), act ? P.White : P.A(P.White, 0.65f), 17);
                    _r.TextC($"${sd.Chips:N0} chips", bx, cy + 62, cw, _r.CapHeight(14), P.Gold, 14);
                    _r.TextC($"{sd.HandsPlayed} hands played", bx, cy + 82, cw, _r.CapHeight(13), P.A(P.Muted, 0.8f), 13);
                    if (!string.IsNullOrEmpty(sd.LastPlayed))
                        _r.TextC(sd.LastPlayed, bx, cy + 100, cw, _r.CapHeight(12), P.A(P.Muted, 0.55f), 12);
                }
                else
                {
                    _r.TextC("Empty Slot", bx, cy + 44, cw, _r.CapHeight(16), P.A(P.Muted, 0.5f), 16);
                    _r.TextC("Start a new run", bx, cy + 70, cw, _r.CapHeight(13), P.A(P.Muted, 0.4f), 13);
                }

                int fi = i;
                _hits.Add(new HitRect(bx, cy, cw, ch, () => { _g.SlotSel = fi; LoadSlot(fi); }));
            }

            // Delete slot buttons
            for (int i = 0; i < 3; i++)
            {
                if (!SaveData.Exists(i)) continue;
                float bx  = sx + i * (cw + gap);
                float dbx = bx + cw - 22, dby = cy + 4, dbw = 18, dbh = 18;
                bool  hov = _mx >= dbx && _mx <= dbx + dbw && _my >= dby && _my <= dby + dbh;
                _r.Rect(dbx, dby, dbw, dbh, hov ? P.A(P.Red, 0.8f) : P.A(P.Red, 0.35f));
                _r.TextC("X", dbx, dby, dbw, dbh, P.White, 11);
                int fi = i;
                _hits.Add(new HitRect(dbx, dby, dbw, dbh, () =>
                {
                    SaveData.Delete(fi);
                    _slotNames[fi] = $"Run {fi + 1}";
                }));
            }

            CentreText("Arrow Keys + Enter  or  Click to select", cy + ch + 28, P.A(P.Muted, 0.45f), 13);
        }

        // ── menu ──────────────────────────────────────────────
        private void DrawMenu()
        {
            _r.Rect(0, 0, W, H - HUD_H, P.A(P.Black, 0.55f));

            // title with glow layers + shadow
            float titY = H * 0.18f;
            float titBob = (float)Math.Sin(_now * 1.1) * 3f;
            float titX = (W - _r.Measure("BLACKJACK", 52)) / 2f;
            // glow
            _r.Text("BLACKJACK", titX - 2, titY + titBob - 2, P.A(P.Gold, 0.18f), 52);
            _r.Text("BLACKJACK", titX + 2, titY + titBob + 2, P.A(P.Gold, 0.18f), 52);
            // shadow
            _r.Text("BLACKJACK", titX + 3, titY + titBob + 4, P.A(P.Black, 0.75f), 52);
            // main
            _r.Text("BLACKJACK", titX, titY + titBob, P.Gold, 52);

            float subY = titY + titBob + _r.CapHeight(52) + 12;
            ShadowText("6-Deck Shoe  ·  Vegas Rules",
                (W - _r.Measure("6-Deck Shoe  ·  Vegas Rules", 16)) / 2f,
                subY, P.A(P.Muted, 0.85f), 16, 1, 1);

            string[] labels = { "PLAY", "BUY CHIPS", "STATS", "SLOTS", "QUIT" };
            string[] subs   = { "Deal a new round", "Add more chips", "Session history", "Change save slot", "Exit the game" };
            Vector4[] cols  = { P.Green, P.Gold, P.Blue, P.Purple, P.Red };

            float cw = 190, ch = 86, gap = 18;
            float totalW = labels.Length * cw + (labels.Length - 1) * gap;
            float sx = (W - totalW) / 2f;
            float baseY = H / 2f - ch / 2f;

            for (int i = 0; i < labels.Length; i++)
            {
                float bx    = sx + i * (cw + gap);
                bool  kbSel = _g.MenuSel == i;
                bool  hov   = _mx >= bx && _mx <= bx + cw && _my >= baseY - 12 && _my <= baseY + ch + 12;
                bool  act   = kbSel || hov;
                int   fi    = i;

                // each card bobs on its own phase; active cards bob more
                float phase  = i * 0.72f;
                float bobAmt = act ? 7f : 3f;
                float speed  = act ? 2.2f : 1.4f;
                float bob    = (float)Math.Sin(_now * speed + phase) * bobAmt;
                float cy     = baseY + bob;

                DrawMenuCard(bx, cy, cw, ch, cols[i], act, labels[i], subs[i]);
                _hits.Add(new HitRect(bx, cy - 12, cw, ch + 24, () => DoMenuSel(fi)));
            }

            CentreTextShadow("Arrow Keys + Enter  or  Click", baseY + ch + 28, P.A(P.Muted, 0.55f), 14);
        }

        // ── betting overlay ───────────────────────────────────
        private void DrawBettingOverlay()
        {
            float cx = (W - SIDE_W) / 2f;
            float cy = (H - HUD_H) / 2f - 20;

            string lbl = "CURRENT BET";
            string bs  = $"${_g.BetAmount:N0}";
            float bsz  = 28f;

            float lw  = _r.Measure(lbl, 12);
            float bw  = _r.Measure(bs, bsz);
            float bch = _r.CapHeight(bsz);
            float lch = _r.CapHeight(12);

            float padX = 22f, padY = 14f, gapY = 8f;
            float boxW = Math.Max(lw, bw) + padX * 2;
            float boxH = lch + gapY + bch + padY * 2;
            float bx   = cx - boxW / 2f;
            float by   = cy - boxH / 2f;

            _r.Rect(bx, by, boxW, boxH, P.A(P.Black, 0.55f));
            Border(bx, by, boxW, boxH, P.A(P.Gold, 0.45f), 2);

            _r.Text(lbl, cx - lw / 2f, by + padY,            P.A(P.Gold, 0.75f), 12);
            _r.Text(bs,  cx - bw / 2f, by + padY + lch + gapY, P.White, bsz);
        }

        // ── betting side panel ────────────────────────────────
        private void DrawBettingSidePanel()
        {
            float px = W - SIDE_W;
            float py = 18;
            float ph = H - HUD_H - 18;

            _r.Rect(px, py, SIDE_W, ph, P.A(P.SidePanel, 0.97f));
            Border(px, py, SIDE_W, ph, P.A(P.Gold, 0.4f), 2);
            _r.Rect(px - 22, py + 60, 22, 60, P.A(P.SidePanel, 0.97f));
            Border(px - 22, py + 60, 22, 60, P.A(P.Gold, 0.4f), 2);
            _r.Rect(px, py + 60, 2, 60, P.A(P.SidePanel, 0.97f));
            float tlh = _r.CapHeight(12);
            _r.Text("BET", px - 18, py + 60 + (60 - tlh) / 2f, P.A(P.Gold, 0.6f), 12);

            float headerH = 40;
            _r.Rect(px, py, SIDE_W, headerH, P.A(P.Gold, 0.12f));
            _r.TextC("PLACE YOUR BET", px, py, SIDE_W, headerH, P.Gold, 16);

            (int val, Vector4 chipCol)[] chips = {
                (5,   P.ChipRed),   (10,  P.ChipBlue),
                (25,  P.ChipGreen), (50,  P.ChipBlack),
                (100, P.ChipPurple),(500, P.ChipOrange),
            };

            float chipD = 64f, chipGap = 14f;
            float gridW = chipD * 2 + chipGap;
            float gridX = px + (SIDE_W - gridW) / 2f;
            float gridY = py + headerH + 16;

            for (int i = 0; i < chips.Length; i++)
            {
                var (val, cc) = chips[i];
                int   col = i % 2, row = i / 2;
                float cx2 = gridX + col * (chipD + chipGap);
                float cy2 = gridY + row * (chipD + chipGap);
                bool  hov = _mx >= cx2 && _mx <= cx2 + chipD && _my >= cy2 && _my <= cy2 + chipD;

                float rr = 10f;
                RoundRect(cx2, cy2, chipD, chipD, rr, hov ? Brighten(cc, 0.25f) : cc);

                string vt = val >= 100 ? $"{val}" : $"${val}";
                float  vw = _r.Measure(vt, 14), vh = _r.CapHeight(14);
                _r.Text(vt, cx2 + (chipD - vw) / 2f, cy2 + (chipD - vh) / 2f, P.White, 14);

                int cap = val;
                _hits.Add(new HitRect(cx2, cy2, chipD, chipD, () =>
                {
                    _g.BetAmount = Math.Min(_g.Chips, _g.BetAmount + cap);
                    _audio.Play("chip");
                }));
            }

            float btnRowY = gridY + 3 * (chipD + chipGap) + 4;
            float smBW = (SIDE_W - 40) / 2f, smBH = 34;
            Btn(px + 12,        btnRowY, smBW, smBH, "CLEAR", P.RedDim, () => _g.BetAmount = 0, 14);
            Btn(px + 20 + smBW, btnRowY, smBW, smBH, "-$5",   P.Dim,    () => _g.BetAmount = Math.Max(0, _g.BetAmount - 5), 14);

            float dealY = btnRowY + smBH + 12;
            float dealH = 50;
            bool dealHov = _mx >= px + 12 && _mx <= px + SIDE_W - 12 && _my >= dealY && _my <= dealY + dealH;
            var  dealBg  = _g.BetAmount > 0 && _g.BetAmount <= _g.Chips
                         ? (dealHov ? P.Green : P.GreenDim)
                         : P.A(P.Dim, 0.4f);
            _r.Rect(px + 12, dealY, SIDE_W - 24, dealH, dealBg);
            Border(px + 12, dealY, SIDE_W - 24, dealH, dealHov ? P.White : P.A(P.Green, 0.6f), 2);
            _r.TextC("DEAL", px + 12, dealY, SIDE_W - 24, dealH, P.White, 22);
            if (_g.BetAmount > 0 && _g.BetAmount <= _g.Chips)
                _hits.Add(new HitRect(px + 12, dealY, SIDE_W - 24, dealH, DoDeal));

            float menuY = dealY + dealH + 10;
            Btn(px + 12, menuY, SIDE_W - 24, 34, "< MENU", P.Dim, () => _g.Phase = Phase.Menu, 14);
        }

        // ── cards ─────────────────────────────────────────────
        private void DrawCards()
        {
            float ty = 18, tw = W - 60, th = H - HUD_H - 18;
            float feltBottom = ty + th - RAIL_H;
            float divY    = ty + th * 0.48f;
            float playerH = feltBottom - divY - 10;

            float dcW = 76, dcH = 108;
            float dcY = ty + 14;
            int   dc  = _g.Dealer.Cards.Count;
            float dSpan = dc > 0 ? dcW + (dc - 1) * (dcW * 0.52f) : dcW;
            float dX    = (W - dSpan) / 2f;

            if (_g.DealerShown)
            {
                string ds  = _g.Dealer.ScoreText();
                float  dsW = _r.Measure(ds, 20) + 16, dsH = _r.CapHeight(20) + 8;
                var    dsC = _g.Dealer.IsBust() ? P.Red : P.A(P.Black, 0.7f);
                _r.Rect((W - dsW) / 2f, dcY, dsW, dsH, dsC);
                Border((W - dsW) / 2f, dcY, dsW, dsH, P.A(P.Gold, 0.5f), 1);
                _r.TextC(ds, (W - dsW) / 2f, dcY, dsW, dsH, P.White, 20);
                dcY += dsH + 6;
            }
            else
            {
                float dlw = _r.Measure("DEALER", 12);
                _r.Text("DEALER", (W - dlw) / 2f, dcY, P.A(P.Gold, 0.4f), 12);
                dcY += _r.CapHeight(12) + 8;
            }
            DrawHandCards(_g.Dealer.Cards, dX, dcY, dcW, dcH);

            float pcW = 76, pcH = 108;
            int   cnt = _g.Hands.Count;
            float pZoneX = 40f, pZoneW = W - 80f;
            float slotW  = pZoneW / Math.Max(cnt, 1);
            float pTop   = divY + 10;

            for (int i = 0; i < cnt; i++)
            {
                var  hand   = _g.Hands[i];
                bool active = _g.Phase == Phase.PlayerTurn && i == _g.ActiveHand;
                float hx = pZoneX + i * slotW;
                float hw = slotW - 8;

                if (active)
                {
                    _r.Rect(hx, pTop, hw, playerH - 4, P.A(P.Gold, 0.06f));
                    Border(hx, pTop, hw, playerH - 4, P.A(P.Gold, 0.45f), 2);
                }

                float curY = pTop + 6;

                string hLbl = cnt > 1 ? $"HAND {i + 1}" : "YOUR HAND";
                _r.Text(hLbl, hx + 6, curY, active ? P.Gold : P.A(P.White, 0.45f), 13);

                string betTxt = $"${hand.Bet}";
                if (hand.IsDoubledDown) betTxt += " x2";
                else if (hand.IsSplit)  betTxt += " spl";
                float btW = _r.Measure(betTxt, 12) + 8, btH = _r.CapHeight(12) + 6;
                _r.Rect(hx + hw - btW - 4, curY - 2, btW, btH, P.A(P.GoldDim, 0.8f));
                _r.TextC(betTxt, hx + hw - btW - 4, curY - 2, btW, btH, P.Gold, 12);
                curY += btH + 4;

                if (!hand.Cards.Any(c => c.FaceDown))
                {
                    string sc  = hand.ScoreText();
                    float  scW = _r.Measure(sc, 24) + 14, scH = _r.CapHeight(24) + 8;
                    var    scC = hand.IsBust()      ? P.Red
                               : hand.IsBlackjack() ? P.A(P.Gold, 0.9f)
                               : hand.Score() == 21 ? P.GreenDim
                               : P.A(P.Black, 0.65f);
                    _r.Rect(hx + 6, curY, scW, scH, scC);
                    Border(hx + 6, curY, scW, scH, P.A(P.White, 0.12f), 1);
                    _r.TextC(sc, hx + 6, curY, scW, scH, P.White, 24);
                    curY += scH + 4;
                }

                DrawHandCards(hand.Cards, hx + 6, curY, pcW, pcH);
                float statusY = curY + pcH + 4;
                if (hand.IsBust())       _r.Text("BUST",       hx + 6, statusY, P.Red,  18);
                else if (hand.IsBlackjack()) _r.Text("BLACKJACK!", hx + 6, statusY, P.Gold, 18);
            }
        }

        private static float Ease(float t) { t = Math.Clamp(t, 0f, 1f); return 1f - (1f-t)*(1f-t)*(1f-t); }

        private void DrawHandCards(List<Card> cards, float x, float y, float cw, float ch)
        {
            float step = cw * 0.52f;
            float dur  = 0.22f;

            for (int i = 0; i < cards.Count; i++)
            {
                var   c     = cards[i];
                float destX = x + i * step;
                float destY = y;
                float t     = Ease((float)(_now - c.DealTime) / dur);
                float offY  = c.IsDealer ? -(H * 0.45f) : (H * 0.45f);
                float cx    = destX;
                float cy    = destY + offY * (1f - t);
                float alpha = t;

                if (c.FaceDown)
                {
                    _r.Rect(cx + 3, cy + 4, cw, ch, P.A(P.Black, 0.4f * alpha));
                    _r.Rect(cx, cy, cw, ch, P.A(P.CardBack, alpha));
                    for (int s = 0; s < 6; s++)
                    {
                        float sx = cx + 4 + s * (cw / 6f);
                        _r.Rect(sx, cy + 4, 3, ch - 8, P.A(P.CardBackStripe, 0.6f * alpha));
                    }
                    Border(cx, cy, cw, ch, P.A(P.White, 0.2f * alpha), 2);
                }
                else
                {
                    var fg = c.IsRed() ? P.CardRed : P.CardBlack;

                    _r.Rect(cx + 3, cy + 4, cw, ch, P.A(P.Black, 0.4f * alpha));
                    _r.Rect(cx, cy, cw, ch, P.A(P.CardFace, alpha));
                    Border(cx, cy, cw, ch, P.A(P.CardBorder, alpha), 1);

                    // ── corner rank (top-left) ────────────────
                    float rkSz  = cw * 0.24f;
                    float pad   = 4f;
                    string rank = c.RankGlyph();
                    _r.Text(rank, cx + pad, cy + pad, P.A(fg, alpha), rkSz);

                    // ── small suit icon below rank (top-left) ─
                    float suitSzSmall = cw * 0.16f;
                    float suitY = cy + pad + _r.CapHeight(rkSz) + 2;
                    DrawSuit(c.Suit, cx + pad + _r.Measure(rank, rkSz) / 2f - suitSzSmall * 0.5f,
                             suitY, suitSzSmall, P.A(fg, alpha));

                    // ── centre suit (large) ───────────────────
                    float suitSzBig = cw * 0.38f;
                    float midX = cx + cw / 2f;
                    float midY = cy + ch / 2f;
                    DrawSuit(c.Suit, midX - suitSzBig / 2f, midY - suitSzBig * 0.55f, suitSzBig, P.A(fg, alpha));

                    // ── centre rank label (small, below suit) ─
                    float cRkSz = cw * 0.22f;
                    float cRkW  = _r.Measure(rank, cRkSz);
                    _r.Text(rank, midX - cRkW / 2f, midY + suitSzBig * 0.5f + 2, P.A(fg, alpha * 0.7f), cRkSz);
                }
            }
        }

        // ── geometric suit drawing ─────────────────────────────
        // All suits drawn from (x, y) as top-left of a sz×sz bounding box.
        private void DrawSuit(Suit suit, float x, float y, float sz, Vector4 col)
        {
            switch (suit)
            {
                case Suit.Hearts:   DrawHeart(x, y, sz, col);   break;
                case Suit.Diamonds: DrawDiamond(x, y, sz, col); break;
                case Suit.Clubs:    DrawClub(x, y, sz, col);    break;
                case Suit.Spades:   DrawSpade(x, y, sz, col);   break;
            }
        }

        private void DrawHeart(float x, float y, float sz, Vector4 col)
        {
            // Two upper circles + bottom triangle wedge
            float r = sz * 0.27f;
            float cx = x + sz * 0.5f;
            // left lobe
            DrawDisc(x + r,      y + r,      r, col);
            // right lobe
            DrawDisc(x + sz - r, y + r,      r, col);
            // centre fill between lobes
            _r.Rect(x + r * 0.5f, y, sz - r, r * 1.4f, col);
            // bottom triangle
            int steps = 12;
            float tipX = cx, tipY = y + sz;
            float leftX = x, leftY = y + r * 1.2f;
            float rightX = x + sz, rightY = y + r * 1.2f;
            for (int i = 0; i < steps; i++)
            {
                float t0 = i / (float)steps, t1 = (i + 1) / (float)steps;
                float ax = leftX + (tipX - leftX) * t0, ay = leftY + (tipY - leftY) * t0;
                float bx = leftX + (tipX - leftX) * t1, by2 = leftY + (tipY - leftY) * t1;
                float ex = rightX + (tipX - rightX) * t0, ey = rightY + (tipY - rightY) * t0;
                float fx = rightX + (tipX - rightX) * t1, fy = rightY + (tipY - rightY) * t1;
                float mnX = Math.Min(Math.Min(ax,bx), Math.Min(ex,fx));
                float mnY = Math.Min(Math.Min(ay,by2), Math.Min(ey,fy));
                float mxX = Math.Max(Math.Max(ax,bx), Math.Max(ex,fx));
                float mxY = Math.Max(Math.Max(ay,by2), Math.Max(ey,fy));
                _r.Rect(mnX, mnY, mxX - mnX + 1, mxY - mnY + 1, col);
            }
        }

        private void DrawDiamond(float x, float y, float sz, Vector4 col)
        {
            float cx = x + sz / 2f, cy = y + sz / 2f;
            int steps = 16;
            float[] px = { cx, x + sz, cx, x };
            float[] py = { y, cy, y + sz, cy };
            for (int i = 0; i < 4; i++)
            {
                int ni = (i + 1) % 4;
                float ax = px[i], ay = py[i], bx = px[ni], by2 = py[ni];
                for (int s = 0; s < steps; s++)
                {
                    float t0 = s / (float)steps, t1 = (s + 1) / (float)steps;
                    float ex = ax + (cx - ax) * t0, ey = ay + (cy - ay) * t0;
                    float fx = ax + (cx - ax) * t1, fy = ay + (cy - ay) * t1;
                    float gx = bx + (cx - bx) * t0, gy = by2 + (cy - by2) * t0;
                    float hx = bx + (cx - bx) * t1, hy = by2 + (cy - by2) * t1;
                    float mnX = Math.Min(Math.Min(ex,fx), Math.Min(gx,hx));
                    float mnY = Math.Min(Math.Min(ey,fy), Math.Min(gy,hy));
                    float mxX = Math.Max(Math.Max(ex,fx), Math.Max(gx,hx));
                    float mxY = Math.Max(Math.Max(ey,fy), Math.Max(gy,hy));
                    _r.Rect(mnX, mnY, mxX - mnX + 1, mxY - mnY + 1, col);
                }
            }
            // solid fill via scanline-style strips
            for (int row = 0; row <= steps; row++)
            {
                float t = row / (float)steps;
                float lx, rx, ry;
                if (t < 0.5f)
                {
                    float tt = t * 2f;
                    lx = x + (cx - x) * tt;
                    rx = cx + (x + sz - cx) * tt;
                    ry = y + (cy - y) * tt;
                }
                else
                {
                    float tt = (t - 0.5f) * 2f;
                    lx = cx + (x - cx) * tt;
                    rx = x + sz + (cx - (x+sz)) * tt;
                    ry = cy + (y + sz - cy) * tt;
                }
                _r.Rect(lx, ry, rx - lx, 1, col);
            }
        }

        private void DrawClub(float x, float y, float sz, Vector4 col)
        {
            float cx = x + sz / 2f;
            float r  = sz * 0.23f;
            // three balls
            DrawDisc(cx,       y + r,           r, col);
            DrawDisc(cx - r,   y + r * 2.4f,    r, col);
            DrawDisc(cx + r,   y + r * 2.4f,    r, col);
            // stem
            float stemW = sz * 0.14f, stemH = sz * 0.28f;
            _r.Rect(cx - stemW / 2f, y + sz - stemH, stemW, stemH, col);
            // foot
            _r.Rect(cx - sz * 0.22f, y + sz - stemH * 0.4f, sz * 0.44f, stemH * 0.3f, col);
        }

        private void DrawSpade(float x, float y, float sz, Vector4 col)
        {
            float cx = x + sz / 2f;
            // inverted heart top (upside-down lobes)
            float r = sz * 0.25f;
            float ballY = y + sz * 0.32f;
            DrawDisc(cx - r * 0.6f, ballY, r, col);
            DrawDisc(cx + r * 0.6f, ballY, r, col);
            // pointed top
            int steps = 12;
            float tipX = cx, tipY = y;
            float leftX = x, leftY = ballY + r * 0.6f;
            float rightX = x + sz, rightY = ballY + r * 0.6f;
            for (int i = 0; i < steps; i++)
            {
                float t0 = i / (float)steps, t1 = (i + 1) / (float)steps;
                float ax = leftX + (tipX - leftX) * t0, ay = leftY + (tipY - leftY) * t0;
                float bx = leftX + (tipX - leftX) * t1, by2 = leftY + (tipY - leftY) * t1;
                float ex = rightX + (tipX - rightX) * t0, ey = rightY + (tipY - rightY) * t0;
                float fx = rightX + (tipX - rightX) * t1, fy = rightY + (tipY - rightY) * t1;
                float mnX = Math.Min(Math.Min(ax,bx), Math.Min(ex,fx));
                float mnY = Math.Min(Math.Min(ay,by2), Math.Min(ey,fy));
                float mxX = Math.Max(Math.Max(ax,bx), Math.Max(ex,fx));
                float mxY = Math.Max(Math.Max(ay,by2), Math.Max(ey,fy));
                _r.Rect(mnX, mnY, mxX - mnX + 1, mxY - mnY + 1, col);
            }
            // fill centre
            _r.Rect(cx - r * 0.55f, ballY - r * 0.3f, r * 1.1f, r * 1.6f, col);
            // stem + foot
            float stemW = sz * 0.14f, stemH = sz * 0.22f;
            _r.Rect(cx - stemW / 2f, y + sz - stemH, stemW, stemH, col);
            _r.Rect(cx - sz * 0.22f, y + sz - stemH * 0.4f, sz * 0.44f, stemH * 0.35f, col);
        }

        // ── action bar ────────────────────────────────────────
        private double LastDealTime()
        {
            double t = 0;
            foreach (var c in _g.Dealer.Cards) t = Math.Max(t, c.DealTime);
            foreach (var hand in _g.Hands) foreach (var c in hand.Cards) t = Math.Max(t, c.DealTime);
            return t;
        }

        private void DrawActionBar()
        {
            var hand = CurHand();
            if (hand == null) return;

            float barDur = 0.18f, barDelay = 0.22f;
            float barT   = Ease((float)(_now - LastDealTime() - barDelay) / barDur);
            float slide  = (1f - barT) * (ACT_H + RAIL_H);
            float areaY  = H - HUD_H - RAIL_H - ACT_H + slide;

            _r.Rect(0, areaY, W, ACT_H + RAIL_H, P.A(P.Black, 0.72f * barT));
            _r.Rect(0, areaY, W, 2, P.A(P.Gold, 0.5f * barT));

            var acts = new (string lbl, string key, bool avail, Action act)[]
            {
                ("HIT",    "H", true,
                    () => { hand.AddCard(DealCard()); if (hand.IsBust()) NextHand(); }),
                ("STAND",  "S", true, NextHand),
                ("DOUBLE", "D", hand.Cards.Count == 2 && _g.Chips >= hand.Bet, () => DoDouble(hand)),
                ("SPLIT",  "P", hand.CanSplit() && _g.Hands.Count < 4 && _g.Chips >= hand.Bet, () => DoSplit(hand)),
            };

            float bw = 176f, bh = 60f, gap = 14f;
            float totalBW = acts.Length * bw + (acts.Length - 1) * gap;
            float startX  = (W - totalBW) / 2f;
            float by      = areaY + (ACT_H - bh) / 2f;

            for (int i = 0; i < acts.Length; i++)
            {
                var (lbl, key, avail, act) = acts[i];
                float bx  = startX + i * (bw + gap);
                bool  hov = _mx >= bx && _mx <= bx + bw && _my >= by && _my <= by + bh;

                var bgC = !avail ? P.A(P.Dim, 0.25f) : hov ? P.Green : P.GreenDim;
                var fgC = avail ? P.White : P.A(P.Muted, 0.4f);
                var bdr = !avail ? P.A(P.Dim, 0.2f) : hov ? P.A(P.White, 0.8f) : P.A(P.Green, 0.5f);

                _r.Rect(bx, by, bw, bh, bgC);
                Border(bx, by, bw, bh, bdr, 2);
                _r.Text(key, bx + 8, by + 6, P.A(fgC, 0.45f), 11);
                _r.TextC(lbl, bx, by, bw, bh, fgC, 20);

                if (avail) { var cap = act; _hits.Add(new HitRect(bx, by, bw, bh, cap)); }
            }
        }

        // ── results bar ───────────────────────────────────────
        private void DrawResultsBar()
        {
            float barDur = 0.18f, barDelay = 0.28f;
            float barT   = Ease((float)(_now - LastDealTime() - barDelay) / barDur);
            float slide  = (1f - barT) * (ACT_H + RAIL_H);
            float areaY  = H - HUD_H - RAIL_H - ACT_H + slide;

            _r.Rect(0, areaY, W, ACT_H + RAIL_H, P.A(P.Black, 0.78f * barT));
            _r.Rect(0, areaY, W, 2, P.A(P.Gold, 0.7f * barT));

            int   n     = Math.Min(_g.Results.Count, 4);
            float cbW   = 210f, cbH = 52f;
            float msgW  = W - cbW - 60f;
            float slotW = msgW / Math.Max(n, 1);

            for (int i = 0; i < n; i++)
            {
                string msg = _g.Results[i];
                var col = msg.Contains("WIN") || msg.Contains("BLACKJACK") ? P.Green
                        : msg.Contains("BUST") || msg.Contains("LOSE")     ? P.Red
                        : msg.Contains("PUSH")                              ? P.Gold
                        : P.White;
                _r.TextC(msg, 40 + i * slotW, areaY, slotW, ACT_H, P.A(col, barT), 16);
            }

            float cbX = W - cbW - 30f;
            float cbY = areaY + (ACT_H - cbH) / 2f;
            Btn(cbX, cbY, cbW, cbH, "CONTINUE →", P.Purple, () =>
            {
                if (_g.AutoPlay) { _g.BetAmount = Math.Min(_g.BetAmount, _g.Chips); _g.Phase = Phase.Betting; }
                else _g.Phase = Phase.Menu;
            }, 18);
        }

        // ── buy chips ─────────────────────────────────────────
        private void DrawBuyChips()
        {
            _r.Rect(0, 0, W, H, P.A(P.Black, 0.6f));
            float pw = 420, ph = 310;
            float px = (W - pw) / 2f, py = (H - ph) / 2f;
            _r.Rect(px, py, pw, ph, P.Panel);
            Border(px, py, pw, ph, P.A(P.Gold, 0.5f), 2);
            _r.TextC("BUY CHIPS", px, py, pw, 44, P.Gold, 18);

            string[] lbls = { "+$500", "+$1,000", "+$5,000" };
            int[]    cost = { 0, 0, 0 };
            for (int i = 0; i < 3; i++)
            {
                float bx2 = px + 20, by2 = py + 54 + i * 68;
                float bw2 = pw - 40, bh2 = 56;
                bool  sel = _g.BuySel == i;
                bool  hov = _mx >= bx2 && _mx <= bx2 + bw2 && _my >= by2 && _my <= by2 + bh2;
                _r.Rect(bx2, by2, bw2, bh2, (sel || hov) ? P.A(P.GreenDim, 0.6f) : P.A(P.Black, 0.3f));
                Border(bx2, by2, bw2, bh2, (sel || hov) ? P.Green : P.Dim, 2);
                _r.TextC(lbls[i], bx2, by2, bw2, bh2, P.White, 18);
                int oi = i;
                _hits.Add(new HitRect(bx2, by2, bw2, bh2, () => DoBuy(oi)));
            }
            Btn(px + 20, py + ph - 52, pw - 40, 38, "CANCEL", P.Dim, () => _g.Phase = Phase.Menu, 15);
        }

        // ── stats ─────────────────────────────────────────────
        private void DrawStats()
        {
            _r.Rect(0, 0, W, H, P.A(P.Black, 0.6f));
            float pw = 460, ph = 340;
            float px = (W - pw) / 2f, py = (H - ph) / 2f;
            _r.Rect(px, py, pw, ph, P.Panel);
            Border(px, py, pw, ph, P.A(P.Gold, 0.5f), 2);
            _r.TextC("STATISTICS", px, py, pw, 44, P.Gold, 18);

            double pct = _g.HandsPlayed > 0 ? _g.HandsWon * 100.0 / _g.HandsPlayed : 0;
            (string k, string v, Vector4 vc)[] rows = {
                ("Hands Played",  _g.HandsPlayed.ToString(),                     P.White),
                ("Hands Won",     _g.HandsWon.ToString(),                        P.Green),
                ("Win Rate",      $"{pct:F1}%",                                  P.Blue),
                ("Net Winnings",  $"{(_g.NetWinnings >= 0 ? "+" : "")}${_g.NetWinnings:N0}", _g.NetWinnings >= 0 ? P.Green : P.Red),
                ("Current Chips", $"${_g.Chips:N0}",                             P.Gold),
            };

            float ry = py + 52;
            foreach (var (k, v, vc) in rows)
            {
                _r.Text(k, px + 24, ry, P.Muted, 16);
                _r.TextR(v, px + pw - 24, ry, vc, 16);
                ry += 38;
            }
            Btn(px + 20, py + ph - 52, pw - 40, 38, "CLOSE", P.Dim, () => _g.Phase = Phase.Menu, 15);
        }

        // ── ui helpers ────────────────────────────────────────
        private void DrawMenuCard(float x, float y, float w, float h, Vector4 accent, bool active, string title, string sub)
        {
            _r.Rect(x, y, w, h, active ? P.A(accent, 0.22f) : P.A(P.Black, 0.60f));
            Border(x, y, w, h, active ? accent : P.A(accent, 0.4f), active ? 3 : 1);
            // inner top highlight strip when active
            if (active) _r.Rect(x + 2, y + 2, w - 4, 3, P.A(accent, 0.35f));
            ShadowTextC(title, x, y,            w, h * 0.55f, active ? Brighten(accent, 0.15f) : P.A(P.Muted, 0.9f), active ? 18 : 17);
            ShadowTextC(sub,   x, y + h * 0.5f, w, h * 0.5f, P.A(P.Muted, active ? 0.85f : 0.60f), 13);
        }

        private void DrawDisc(float cx, float cy, float r, Vector4 col)
        {
            int segs = 24;
            for (int i = 0; i < segs; i++)
            {
                double a0 = Math.PI * 2 * i / segs;
                double a1 = Math.PI * 2 * (i + 1) / segs;
                float x1 = cx + r * (float)Math.Cos(a0), y1 = cy + r * (float)Math.Sin(a0);
                float x2 = cx + r * (float)Math.Cos(a1), y2 = cy + r * (float)Math.Sin(a1);
                float mnX = Math.Min(cx, Math.Min(x1, x2)), mnY = Math.Min(cy, Math.Min(y1, y2));
                float mxX = Math.Max(cx, Math.Max(x1, x2)), mxY = Math.Max(cy, Math.Max(y1, y2));
                _r.Rect(mnX, mnY, mxX - mnX + 1, mxY - mnY + 1, col);
            }
        }

        private static Vector4 Brighten(Vector4 c, float amt) =>
            new(Math.Min(1f, c.X + amt), Math.Min(1f, c.Y + amt), Math.Min(1f, c.Z + amt), c.W);

        private void Border(float x, float y, float w, float h, Vector4 col, float t = 2)
        {
            _r.Rect(x,     y,     w,  t, col);
            _r.Rect(x,     y+h-t, w,  t, col);
            _r.Rect(x,     y,     t,  h, col);
            _r.Rect(x+w-t, y,     t,  h, col);
        }

        private void RoundRect(float x, float y, float w, float h, float r, Vector4 col)
        {
            _r.Rect(x + r, y,     w - r * 2, h,         col);
            _r.Rect(x,     y + r, r,          h - r * 2, col);
            _r.Rect(x+w-r, y + r, r,          h - r * 2, col);
            DrawDisc(x + r,     y + r,     r, col);
            DrawDisc(x + w - r, y + r,     r, col);
            DrawDisc(x + r,     y + h - r, r, col);
            DrawDisc(x + w - r, y + h - r, r, col);
        }

        private void Btn(float x, float y, float w, float h, string lbl, Vector4 col, Action onClick, float sz = 18f)
        {
            bool hov    = _mx >= x && _mx <= x + w && _my >= y && _my <= y + h;
            var  active = hov;
            var  accent = col;
            _r.Rect(x, y, w, h, active ? P.A(accent, 0.25f) : P.A(P.Black, 0.4f));
            Border(x, y, w, h, active ? accent : P.A(accent, 0.4f), active ? 3 : 1);
            _r.TextC(lbl, x, y, w, h, active ? P.White : P.A(P.White, 0.65f), sz);
            _hits.Add(new HitRect(x, y, w, h, onClick));
        }

        private void CentreText(string s, float y, Vector4 col, float sz)
        {
            _r.Text(s, (W - _r.Measure(s, sz)) / 2f, y, col, sz);
        }

        // Draw text with a drop shadow for legibility
        private void ShadowText(string s, float x, float y, Vector4 col, float sz, float ox = 2f, float oy = 2f)
        {
            _r.Text(s, x + ox, y + oy, P.A(P.Black, col.W * 0.7f), sz);
            _r.Text(s, x, y, col, sz);
        }

        private void ShadowTextC(string s, float bx, float by, float bw, float bh, Vector4 col, float sz)
        {
            float tw = _r.Measure(s, sz), th = _r.CapHeight(sz);
            float x = bx + (bw - tw) * 0.5f, y = by + (bh - th) * 0.5f;
            ShadowText(s, x, y, col, sz);
        }

        private void CentreTextShadow(string s, float y, Vector4 col, float sz)
        {
            float x = (W - _r.Measure(s, sz)) / 2f;
            ShadowText(s, x, y, col, sz);
        }
    }

    class Program
    {
        static void Main() => new App().Run();
    }
}
