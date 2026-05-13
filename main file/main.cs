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
    //  PLAYER COLORS
    // ═══════════════════════════════════════════════════════════
    static class PlayerColors
    {
        static readonly Vector4[] _cols = {
            new(0.20f, 0.82f, 0.38f, 1f),  // green
            new(0.25f, 0.55f, 1.00f, 1f),  // blue
            new(1.00f, 0.82f, 0.20f, 1f),  // gold
            new(0.90f, 0.15f, 0.20f, 1f),  // red
        };
        public static Vector4 Get(int i) => _cols[i % _cols.Length];
    }

    // ═══════════════════════════════════════════════════════════
    //  APP
    // ═══════════════════════════════════════════════════════════
    class App
    {
        IWindow       _win   = null!;
        GL            _gl    = null!;
        IInputContext _inp   = null!;
        Renderer      _r     = null!;
        Font          _font  = null!;
        GS            _g     = new();
        AudioEngine   _audio = new();

        readonly string[] _slotNames = { "Run 1", "Run 2", "Run 3" };

        float _mx, _my;
        readonly List<HitRect> _hits = new();

        double _now;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        double _menuEnterTime = -999;
        Phase  _prevPhase     = Phase.SlotSelect;

        // Player name editing state
        int    _editingPlayer = -1;
        string _editBuf       = "";

        // Poker state
        PokerGame  _poker = new();
        double     _pokerDealAnimEnd = 0;  // time when latest community card finishes animating

        // LAN networking
        LanHost?   _lanHost;
        LanClient? _lanClient;
        bool       _isLanHost    = false;
        bool       _isLanClient  = false;
        string     _lanHostIP    = "";
        bool       _lanConnected = false;
        string     _lanStatusMsg = "";
        int        _lanSetupMode    = 0;   // 0=local, 1=LAN
        bool       _lanSetupModeSet = false;
        int        _lanHostSeats    = 2;   // seats for LAN host (including self)
        // Snapshot received from host (client mode)
        PokerStateSnapshot? _lanSnap;
        bool       _lanSnapDirty = false;

        // Slots state
        SlotsGame _slots = new();
        int       _slotsChips = 1000;

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
                if (_g.Phase != Phase.SlotSelect && _g.Phase != Phase.PlayerSetup)
                    SaveData.From(_g, _slotNames[_g.SlotIndex]).Save(_g.SlotIndex);
                _r?.Dispose(); _font?.Dispose(); _inp?.Dispose(); _audio.Dispose();
            };
            _win.Run();
        }

        private void OnLoad()
        {
            _gl  = _win.CreateOpenGL();
            _inp = _win.CreateInput();

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
            {
                kb.KeyDown  += (_, k, _) => OnKey(k);
                kb.KeyChar  += (_, c)    => OnChar(c);
            }
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
        private void OnChar(char c)
        {
            if (_editingPlayer < 0) return;
            if (_g.Phase != Phase.PlayerSetup && _g.Phase != Phase.PokerSetup) return;
            if (c < 32 || c > 126) return;
            int maxLen = _editingPlayer == 99 ? 64 : 12; // IP allows longer input
            if (_editBuf.Length < maxLen) _editBuf += c;
        }

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

                case Phase.PlayerSetup:
                    if (_editingPlayer >= 0)
                    {
                        if (k == Key.Backspace && _editBuf.Length > 0)
                            _editBuf = _editBuf[..^1];
                        if (k is Key.Enter or Key.Escape)
                        {
                            if (_editBuf.Trim().Length > 0)
                                _g.Players[_editingPlayer].Name = _editBuf.Trim();
                            _editingPlayer = -1;
                            _editBuf = "";
                        }
                    }
                    else
                    {
                        if (k == Key.Left)  _g.PlayerSetupSel = Math.Max(1, _g.PlayerSetupSel - 1);
                        if (k == Key.Right) _g.PlayerSetupSel = Math.Min(4, _g.PlayerSetupSel + 1);
                        if (k == Key.Escape) { _g.Phase = Phase.Menu; }
                        if (k is Key.Enter or Key.Space) StartGame();
                    }
                    break;

                case Phase.Menu:
                    if (k == Key.Up)   { if (_g.MenuSel < 0) _g.MenuSel = 0; else _g.MenuSel = Math.Max(0, _g.MenuSel - 1); }
                    if (k == Key.Down) { if (_g.MenuSel < 0) _g.MenuSel = 0; else _g.MenuSel = Math.Min(4, _g.MenuSel + 1); }
                    if (k is Key.Enter or Key.Space && _g.MenuSel >= 0) DoMenuSel(_g.MenuSel);
                    if (k == Key.Q) _win.Close();
                    break;

                case Phase.Betting:
                {
                    var p = _g.Players[_g.ActivePlayer];
                    if (k == Key.Left)  p.BetAmount = Math.Max(10,       p.BetAmount - 10);
                    if (k == Key.Right) p.BetAmount = Math.Min(p.Chips,  p.BetAmount + 10);
                    if (k == Key.Down)  p.BetAmount = Math.Max(10,       p.BetAmount - 50);
                    if (k == Key.Up)    p.BetAmount = Math.Min(p.Chips,  p.BetAmount + 50);
                    if (k is Key.Enter or Key.Space) AdvanceBetting();
                    if (k == Key.Escape) _g.Phase = Phase.Menu;
                    break;
                }

                case Phase.PlayerTurn:
                {
                    var hand = CurHand();
                    if (hand == null) { DoDealer(); break; }
                    if (k == Key.H) { hand.AddCard(DealCard()); if (hand.IsBust()) NextHand(); }
                    if (k == Key.S) NextHand();
                    if (k == Key.D && hand.Cards.Count == 2 && _g.Players[_g.ActivePlayer].Chips >= hand.Bet)
                        DoDouble(hand);
                    if (k == Key.P && hand.CanSplit() && _g.Players[_g.ActivePlayer].Hands.Count < 4
                                   && _g.Players[_g.ActivePlayer].Chips >= hand.Bet)
                        DoSplit(hand);
                    if (k == Key.Escape) NextHand();
                    break;
                }

                case Phase.Results:
                    if (k is Key.Enter or Key.Space)
                    {
                        if (_g.AutoPlay) StartNextRound();
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

                case Phase.ModeSelect:
                    if (k == Key.Escape) _g.Phase = Phase.Menu;
                    break;

                case Phase.PokerSetup:
                    if (_editingPlayer >= 0)
                    {
                        if (k == Key.Backspace && _editBuf.Length > 0)
                            _editBuf = _editBuf[..^1];
                        if (k is Key.Enter or Key.Escape)
                        {
                            if (_editingPlayer == 99)
                            {
                                _lanHostIP = _editBuf.Trim();
                            }
                            else if (_editBuf.Trim().Length > 0 && _editingPlayer < _poker.Players.Count)
                            {
                                _poker.Players[_editingPlayer].Name = _editBuf.Trim();
                            }
                            _editingPlayer = -1; _editBuf = "";
                        }
                    }
                    else if (k == Key.Escape) _g.Phase = Phase.ModeSelect;
                    else if (k is Key.Enter or Key.Space) StartPoker();
                    break;

                case Phase.PokerPlay:
                case Phase.PokerShowdown:
                    if (_poker.Phase == PokerPhase.Showdown)
                    {
                        if (k is Key.Enter or Key.Space) PokerNextHand();
                        if (k == Key.Escape) _g.Phase = Phase.ModeSelect;
                    }
                    else if (_poker.IsHumanTurn)
                    {
                        if (k == Key.F) DoPokerHumanAction(PokerAction.Fold);
                        if (k == Key.C) DoPokerHumanAction(
                            _poker.CurrentBet == _poker.ActivePlayer!.Bet ? PokerAction.Check : PokerAction.Call);
                        if (k == Key.R) DoPokerHumanAction(PokerAction.Raise);
                        if (k == Key.A) DoPokerHumanAction(PokerAction.AllIn);
                        if (k == Key.Left)  _poker.RaiseAmount = Math.Max(_poker.BigBlind, _poker.RaiseAmount - _poker.BigBlind);
                        if (k == Key.Right) _poker.RaiseAmount = Math.Min(_poker.ActivePlayer!.Chips, _poker.RaiseAmount + _poker.BigBlind);
                    }
                    if (k == Key.Escape) _g.Phase = Phase.ModeSelect;
                    break;

                case Phase.SlotsPlay:
                    if (k == Key.Escape) _g.Phase = Phase.ModeSelect;
                    if (k is Key.Enter or Key.Space) _slots.Spin(_slotsChips);
                    if (k == Key.Left)  _slots.Bet = Math.Max(1,   _slots.Bet - 1);
                    if (k == Key.Right) _slots.Bet = Math.Min(100, _slots.Bet + 1);
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
            _g.Phase   = Phase.Menu;
            _g.MenuSel = -1;
        }

        private void StartGame()
        {
            // Resize player list to match chosen count, preserving existing names
            int n = _g.PlayerSetupSel;
            while (_g.Players.Count < n) _g.Players.Add(new Player($"Player {_g.Players.Count + 1}"));
            while (_g.Players.Count > n) _g.Players.RemoveAt(_g.Players.Count - 1);
            _g.ActivePlayer = 0;
            _g.Phase = Phase.Betting;
            // Reset all bets to within chip count
            foreach (var p in _g.Players)
                p.BetAmount = Math.Clamp(p.BetAmount, 10, Math.Max(p.Chips, 10));
        }

        private void DoMenuSel(int i)
        {
            switch (i)
            {
                case 0: _g.Phase = Phase.ModeSelect; break;
                case 1: _g.Phase = Phase.BuyChips; _g.BuySel = 0; break;
                case 2: _g.Phase = Phase.Stats; break;
                case 3: _g.Phase = Phase.SlotSelect; break;
                case 4: _win.Close(); break;
            }
        }

        // Betting cycles through each player; after last player dealt
        private void AdvanceBetting()
        {
            var p = _g.Players[_g.ActivePlayer];
            if (p.BetAmount <= 0 || p.BetAmount > p.Chips) return;

            _g.ActivePlayer++;
            if (_g.ActivePlayer >= _g.Players.Count)
            {
                _g.ActivePlayer = 0;
                DoDeal();
            }
        }

        private void DoDeal()
        {
            _g.Dealer      = new Hand();
            _g.DealerShown = false;

            foreach (var p in _g.Players)
            {
                p.Chips -= p.BetAmount;
                p.Hands  = new List<Hand> { new Hand { Bet = p.BetAmount } };
                p.Results.Clear();
            }

            double t    = _now;
            double step = 0.15;
            int    np   = _g.Players.Count;

            // Deal round-robin: p0c0, p1c0 ... dealer0, p0c1, p1c1 ... hole
            for (int i = 0; i < np; i++)
            {
                var c = _g.Deck.Deal();
                c.DealTime = t + i * step;
                c.IsDealer = false;
                _g.Players[i].Hands[0].AddCard(c);
            }
            var d0 = _g.Deck.Deal(); d0.DealTime = t + np * step;       d0.IsDealer = true;
            _g.Dealer.AddCard(d0);
            for (int i = 0; i < np; i++)
            {
                var c = _g.Deck.Deal();
                c.DealTime = t + (np + 1 + i) * step;
                c.IsDealer = false;
                _g.Players[i].Hands[0].AddCard(c);
            }
            var hole = _g.Deck.Deal();
            hole.DealTime = t + (np * 2 + 1) * step;
            hole.IsDealer = true;
            hole.FaceDown = true;
            _g.Dealer.AddCard(hole);

            int totalCards = np * 2 + 2;
            for (int i = 0; i < totalCards; i++)
            {
                int di = i;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep((int)(di * step * 1000));
                    _audio.Play("deal");
                });
            }

            // Check dealer blackjack
            hole.FaceDown = false;
            bool dbj = _g.Dealer.IsBlackjack();
            hole.FaceDown = !dbj;

            if (dbj)
            {
                foreach (var c in _g.Dealer.Cards) c.FaceDown = false;
                _g.DealerShown = true;
                foreach (var p in _g.Players)
                {
                    p.HandsPlayed++;
                    if (p.Hands[0].IsBlackjack())
                    { p.Chips += p.BetAmount; p.Results.Add("PUSH  — both Blackjack"); }
                    else
                    { p.NetWinnings -= p.BetAmount; p.Results.Add("DEALER BLACKJACK  — you lose"); }
                }
                _g.AutoPlay = true;
                _g.ActivePlayer = 0;
                _g.Phase = Phase.Results;
                return;
            }

            _g.ActivePlayer = 0;
            _g.ActiveHand   = 0;

            // Skip players with blackjack
            while (_g.ActivePlayer < _g.Players.Count &&
                   _g.Players[_g.ActivePlayer].Hands[0].IsBlackjack())
                _g.ActivePlayer++;

            if (_g.ActivePlayer >= _g.Players.Count)
            { DoDealer(); return; }

            _g.Phase = Phase.PlayerTurn;
        }

        private void DoDouble(Hand h)
        {
            var p = _g.Players[_g.ActivePlayer];
            p.Chips -= h.Bet;
            h.Bet *= 2;
            h.IsDoubledDown = true;
            h.AddCard(DealCard());
            NextHand();
        }

        private void DoSplit(Hand h)
        {
            var p = _g.Players[_g.ActivePlayer];
            p.Chips -= h.Bet;
            var nh = new Hand { Bet = h.Bet, IsSplit = true };
            h.IsSplit = true;
            nh.AddCard(h.Cards[1]);
            h.Cards.RemoveAt(1);
            h.AddCard(DealCard());
            nh.AddCard(DealCard());
            p.Hands.Insert(_g.ActiveHand + 1, nh);
        }

        private void NextHand()
        {
            _g.ActiveHand++;
            var p = _g.Players[_g.ActivePlayer];
            if (_g.ActiveHand >= p.Hands.Count)
            {
                _g.ActiveHand = 0;
                _g.ActivePlayer++;
                // Skip players with blackjack
                while (_g.ActivePlayer < _g.Players.Count &&
                       _g.Players[_g.ActivePlayer].Hands.All(h => h.IsBlackjack()))
                    _g.ActivePlayer++;

                if (_g.ActivePlayer >= _g.Players.Count)
                    DoDealer();
            }
        }

        private void DoDealer()
        {
            foreach (var c in _g.Dealer.Cards) c.FaceDown = false;
            _g.DealerShown = true;
            bool allBust = _g.Players.All(p => p.Hands.All(h => h.IsBust()));
            if (!allBust)
                while (DealerHits(_g.Dealer))
                    _g.Dealer.AddCard(DealCard(true));
            Settle();
            _g.AutoPlay = true;
            _g.ActivePlayer = 0;
            _g.Phase = Phase.Results;
        }

        private bool DealerHits(Hand d)
        {
            int s = d.Score();
            return s < 17 || (s == 17 && d.IsSoftHand());
        }

        private void Settle()
        {
            int  ds = _g.Dealer.Score();
            bool db = _g.Dealer.IsBust();
            string resultSound = "lose";

            foreach (var p in _g.Players)
            {
                p.Results.Clear();
                for (int i = 0; i < p.Hands.Count; i++)
                {
                    var h   = p.Hands[i];
                    int bet = h.Bet;
                    string pfx = p.Hands.Count > 1 ? $"H{i+1}: " : "";
                    p.HandsPlayed++;

                    if (h.IsBust())
                    { p.Results.Add($"{pfx}BUST  -${bet}"); p.NetWinnings -= bet; resultSound = "bust"; continue; }

                    if (h.IsBlackjack() && !_g.Dealer.IsBlackjack())
                    {
                        int pay = (int)(bet * 1.5);
                        p.Chips += bet + pay;
                        p.Results.Add($"{pfx}BLACKJACK  +${pay}");
                        p.HandsWon++; p.NetWinnings += pay; resultSound = "blackjack"; continue;
                    }

                    int ps = h.Score();
                    if (db || ps > ds)
                    {
                        p.Chips += bet * 2;
                        p.Results.Add($"{pfx}WIN  +${bet}  ({ps} vs {ds})");
                        p.HandsWon++; p.NetWinnings += bet;
                        if (resultSound != "blackjack") resultSound = "win";
                        continue;
                    }
                    if (ps == ds)
                    {
                        p.Chips += bet;
                        p.Results.Add($"{pfx}PUSH  (both {ps})");
                        if (resultSound == "lose") resultSound = "push";
                        continue;
                    }
                    p.Results.Add($"{pfx}LOSE  -${bet}  ({ps} vs {ds})");
                    p.NetWinnings -= bet;
                }
            }

            SaveData.From(_g, _slotNames[_g.SlotIndex]).Save(_g.SlotIndex);
            string snd = resultSound;
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(400); _audio.Play(snd); });
        }

        private void StartNextRound()
        {
            _g.ActivePlayer = 0;
            foreach (var p in _g.Players)
                p.BetAmount = Math.Clamp(p.BetAmount, 10, Math.Max(p.Chips, 10));
            _g.Phase = Phase.Betting;
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
            _g.Players[_g.ActivePlayer].Chips += amt[sel];
            _g.Phase = Phase.Menu;
            SaveData.From(_g, _slotNames[_g.SlotIndex]).Save(_g.SlotIndex);
        }

        private Hand? CurHand()
        {
            if (_g.ActivePlayer >= _g.Players.Count) return null;
            var hands = _g.Players[_g.ActivePlayer].Hands;
            if (_g.ActiveHand >= hands.Count) return null;
            return hands[_g.ActiveHand];
        }

        // ── poker actions ─────────────────────────────────────
        private void StartPoker()
        {
            if (_isLanClient) { _g.Phase = Phase.PokerPlay; return; }
            _poker = new PokerGame();
            int humanCount = _isLanHost ? (_lanHost?.SeatCount ?? 1) : _g.PlayerSetupSel;
            for (int i = 0; i < humanCount; i++)
            {
                string name = _isLanHost && i > 0 && i - 1 < _lanHost!.ClientNames.Count
                    ? _lanHost.ClientNames[i - 1]
                    : (i < _g.Players.Count ? _g.Players[i].Name : $"Player {i+1}");
                _poker.Players.Add(new PokerPlayer(name, 1000, true));
            }
            string[] aiNames = { "Dealer Dan", "Lucky Lou", "River Rita", "Bluff Bill", "All-In Al" };
            for (int i = humanCount; i < 6; i++)
                _poker.Players.Add(new PokerPlayer(aiNames[i - humanCount], 1000, false));
            _poker.DealerIdx  = 0;
            _poker.DealClock  = _now;
            _poker.StartNewHand();
            _pokerDealAnimEnd = _now + _poker.Players.Count * 2 * 0.12 + 0.5;
            if (_isLanHost) _lanHost!.BroadcastState(_poker, _now);
            RunPokerAiUntilHuman();
            _g.Phase = Phase.PokerPlay;
        }

        private void RunPokerAiUntilHuman()
        {
            if (_isLanClient) return;
            int safety = 0;
            while (_poker.Phase != PokerPhase.Showdown
                   && !_poker.WaitingForDeal
                   && !_poker.IsHumanTurn
                   && _poker.ActivePlayer != null
                   && safety++ < 100)
            {
                _poker.DealClock = _now;
                _poker.DoAiAction(_poker.ActiveIdx);
            }
            // If community cards were just dealt, set anim end time
            if (_poker.WaitingForDeal && _poker.Community.Count > 0)
            {
                double lastDeal = _poker.Community.Max(c => c.DealTime);
                _pokerDealAnimEnd = lastDeal + 0.55;
            }
            if (_poker.Phase == PokerPhase.Showdown)
                _g.Phase = Phase.PokerShowdown;
            if (_isLanHost) _lanHost!.BroadcastState(_poker, _now);
        }

        private void PokerNextHand()
        {
            if (_isLanClient) return;
            _poker.Players.RemoveAll(p => !p.IsHuman && p.Chips <= 0);
            bool anyHuman = _poker.Players.Any(p => p.IsHuman && p.Chips > 0);
            if (!anyHuman) { CleanupLan(); _g.Phase = Phase.ModeSelect; return; }
            if (_poker.Players.Count(p => !p.IsOut) == 1) { CleanupLan(); _g.Phase = Phase.ModeSelect; return; }
            _poker.DealClock = _now;
            _poker.StartNewHand();
            _pokerDealAnimEnd = _now + _poker.Players.Count * 2 * 0.12 + 0.5;
            if (_isLanHost) _lanHost!.BroadcastState(_poker, _now);
            RunPokerAiUntilHuman();
            _g.Phase = Phase.PokerPlay;
        }

        private void DoPokerHumanAction(PokerAction action)
        {
            if (_isLanClient)
            {
                _lanClient!.SendAction(action, action == PokerAction.Raise ? _poker.RaiseAmount : 0);
                return;
            }
            if (!_poker.IsHumanTurn) return;
            _poker.DealClock = _now;
            int prevCommunity = _poker.Community.Count;
            _poker.DoAction(_poker.ActiveIdx, action,
                action == PokerAction.Raise ? _poker.RaiseAmount : 0);
            if (_poker.WaitingForDeal && _poker.Community.Count > prevCommunity)
            {
                double lastDeal = _poker.Community.Max(c => c.DealTime);
                _pokerDealAnimEnd = lastDeal + 0.55;
            }
            if (_poker.Phase == PokerPhase.Showdown)
                _g.Phase = Phase.PokerShowdown;
            else
                RunPokerAiUntilHuman();
            if (_isLanHost) _lanHost!.BroadcastState(_poker, _now);
        }

        private void CleanupLan()
        {
            _lanHost?.Dispose();   _lanHost   = null;
            _lanClient?.Dispose(); _lanClient = null;
            _isLanHost = false; _isLanClient = false; _lanConnected = false;
        }

        private void StartLanHost(int seats)
        {
            CleanupLan();
            _lanHost      = new LanHost();
            _isLanHost    = true;
            _lanStatusMsg = $"Hosting on port {LanHost.PORT} — waiting for {seats - 1} player(s)...";
            _lanHost.ActionReceived += act =>
            {
                lock (_poker)
                {
                    if (act.Seat != _poker.ActiveIdx) return;
                    _poker.DealClock = _now;
                    _poker.DoAction(act.Seat, act.Action, act.RaiseAmt);
                    if (_poker.Phase == PokerPhase.Showdown) _g.Phase = Phase.PokerShowdown;
                    else RunPokerAiUntilHuman();
                    _lanHost!.BroadcastState(_poker, _now);
                }
            };
            _lanHost.ClientConnected += seat =>
            {
                _lanStatusMsg = $"Player {seat + 1} connected ({seat}/{seats - 1})";
                if (seat == seats - 1) { _lanConnected = true; _lanStatusMsg = "All players connected!"; }
            };
            _lanHost.Start(seats);
        }

        private void StartLanClient(string ip, string playerName)
        {
            CleanupLan();
            _lanClient    = new LanClient();
            _isLanClient  = true;
            _lanStatusMsg = $"Connecting to {ip}...";
            _lanClient.StateReceived += snap =>
            {
                lock (_poker)
                {
                    _lanSnap      = snap;
                    _lanSnapDirty = true;
                }
            };
            _lanClient.Disconnected += () => { _lanStatusMsg = "Disconnected from host."; _isLanClient = false; };
            bool ok = _lanClient.Connect(ip, playerName);
            _lanStatusMsg = ok ? $"Connected to {ip}!" : $"Could not connect to {ip}";
            if (ok) { _lanConnected = true; _g.Phase = Phase.PokerPlay; }
        }

        private void ApplyLanSnapshot(PokerStateSnapshot snap)
        {
            _poker.Phase       = snap.Phase;
            _poker.Pot         = snap.Pot;
            _poker.CurrentBet  = snap.CurrentBet;
            _poker.ActiveIdx   = snap.ActiveIdx;
            _poker.DealerIdx   = snap.DealerIdx;
            _poker.RaiseAmount = snap.RaiseAmount;
            _poker.ShowdownMsg = snap.ShowdownMsg;
            _poker.Log.Clear(); _poker.Log.AddRange(snap.Log);
            _poker.Winners.Clear();

            // Sync players
            while (_poker.Players.Count < snap.Players.Count)
                _poker.Players.Add(new PokerPlayer("?", 0, false));
            while (_poker.Players.Count > snap.Players.Count)
                _poker.Players.RemoveAt(_poker.Players.Count - 1);

            for (int i = 0; i < snap.Players.Count; i++)
            {
                var s = snap.Players[i]; var p = _poker.Players[i];
                p.Name = s.Name; p.Chips = s.Chips; p.IsHuman = s.IsHuman;
                p.IsFolded = s.IsFolded; p.IsAllIn = s.IsAllIn; p.IsOut = s.IsOut;
                p.Bet = s.Bet; p.LastAction = s.LastAction;
                if (s.IsWinner) _poker.Winners.Add(p);
            }

            // Sync community cards
            for (int i = 0; i < snap.Community.Count; i++)
            {
                var sc = snap.Community[i];
                if (i >= _poker.Community.Count)
                {
                    var nc = new Card(sc.Suit, sc.Rank); nc.DealTime = sc.DealTime; _poker.Community.Add(nc);
                }
                else { _poker.Community[i] = new Card(sc.Suit, sc.Rank); _poker.Community[i].DealTime = sc.DealTime; }
            }
            while (_poker.Community.Count > snap.Community.Count) _poker.Community.RemoveAt(_poker.Community.Count - 1);

            // Sync hole cards
            for (int i = 0; i < snap.Players.Count && i < snap.HoleCards.Count; i++)
            {
                var hp = _poker.Players[i]; hp.HoleCards.Clear();
                foreach (var sc in snap.HoleCards[i])
                {
                    var nc = new Card(sc.Suit, sc.Rank); nc.DealTime = sc.DealTime; hp.HoleCards.Add(nc);
                }
            }

            if (_poker.Phase == PokerPhase.Showdown) _g.Phase = Phase.PokerShowdown;
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
                case Phase.SlotSelect:   DrawSlotSelect();   break;
                case Phase.PlayerSetup:  DrawPlayerSetup();  break;
                case Phase.Menu:         DrawMenu();         break;
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
                case Phase.BuyChips:     DrawBuyChips();   break;
                case Phase.Stats:        DrawStats();      break;
                case Phase.ModeSelect:   DrawModeSelect(); break;
                case Phase.PokerSetup:   DrawPokerSetup(); break;
                case Phase.PokerPlay:
                case Phase.PokerShowdown:
                    // Apply incoming LAN state on main thread
                    if (_lanSnapDirty) { lock(_poker) { if(_lanSnap!=null) ApplyLanSnapshot(_lanSnap); _lanSnapDirty=false; } }
                    // Resume after community card deal animation
                    if (_poker.WaitingForDeal && _now > _pokerDealAnimEnd)
                    {
                        _poker.DealClock = _now;
                        _poker.ResumAfterDeal();
                        RunPokerAiUntilHuman();
                    }
                    DrawPoker();
                    break;
                case Phase.SlotsPlay:
                    DrawSlots();
                    int won = _slots.Tick(_now, 1.0 / 60.0, _slots.Bet);
                    if (won > 0) _slotsChips += won;
                    break;
            }

            bool hideHud = _g.Phase == Phase.SlotSelect || _g.Phase == Phase.PlayerSetup
                        || _g.Phase == Phase.ModeSelect  || _g.Phase == Phase.PokerSetup;
            if (!hideHud) DrawHUD();
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
            float ty = by + (HUD_H - _r.CapHeight(15)) / 2f;

            if (_g.Players.Count == 1)
            {
                var p = _g.Players[0];
                _r.Text($"CHIPS  ${p.Chips:N0}", 24, ty, P.Gold, 17);
                string slotLbl = $"Slot {_g.SlotIndex + 1}: {_slotNames[_g.SlotIndex]}";
                _r.Text(slotLbl, (W - _r.Measure(slotLbl, 13)) / 2f, ty + 2, P.A(P.Muted, 0.6f), 13);
                string info = $"Hands {p.HandsPlayed}   Won {p.HandsWon}   Net {(p.NetWinnings >= 0 ? "+" : "")}${p.NetWinnings:N0}";
                _r.TextR(info, W - 24, ty, P.Muted, 15);
            }
            else
            {
                // Show each player's chips
                float slotW = (float)W / _g.Players.Count;
                for (int i = 0; i < _g.Players.Count; i++)
                {
                    var p   = _g.Players[i];
                    var col = PlayerColors.Get(i);
                    bool act = (_g.Phase == Phase.Betting || _g.Phase == Phase.PlayerTurn)
                               && i == _g.ActivePlayer;
                    if (act) _r.Rect(i * slotW, by, slotW, HUD_H, P.A(col, 0.15f));
                    string lbl = $"{p.Name}  ${p.Chips:N0}";
                    _r.TextC(lbl, i * slotW, by, slotW, HUD_H, act ? col : P.A(col, 0.55f), 14);
                }
            }
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

                // X button drawn on top — add its hit rect BEFORE the card so it wins in Click()
                if (has)
                {
                    float dbx = bx + cw - 22, dby = cy + 4, dbw = 18, dbh = 18;
                    bool  xhov = _mx >= dbx && _mx <= dbx + dbw && _my >= dby && _my <= dby + dbh;
                    _r.Rect(dbx, dby, dbw, dbh, xhov ? P.A(P.Red, 0.8f) : P.A(P.Red, 0.35f));
                    _r.TextC("X", dbx, dby, dbw, dbh, P.White, 11);
                    int fdi = i;
                    _hits.Add(new HitRect(dbx, dby, dbw, dbh, () =>
                    {
                        SaveData.Delete(fdi);
                        _slotNames[fdi] = $"Run {fdi + 1}";
                    }));
                }

                int fi = i;
                _hits.Add(new HitRect(bx, cy, cw, ch, () => { _g.SlotSel = fi; LoadSlot(fi); }));
            }

            CentreText("Arrow Keys + Enter  or  Click to select", cy + ch + 28, P.A(P.Muted, 0.45f), 13);
        }

        // ── player setup ──────────────────────────────────────
        private void DrawPlayerSetup()
        {
            _r.Rect(0, 0, W, H, P.A(P.Black, 0.78f));

            float titY = H * 0.10f;
            CentreText("PLAYER SETUP", titY, P.Gold, 36);
            CentreText("How many players?", titY + _r.CapHeight(36) + 10, P.A(P.Muted, 0.7f), 15);

            // Player count selector
            float selY = titY + _r.CapHeight(36) + 46;
            float btnW = 60, btnH = 50, btnGap = 16;
            float totalBW = 4 * btnW + 3 * btnGap;
            float bsx = (W - totalBW) / 2f;

            for (int i = 1; i <= 4; i++)
            {
                float bx  = bsx + (i - 1) * (btnW + btnGap);
                bool  sel = _g.PlayerSetupSel == i;
                bool  hov = _mx >= bx && _mx <= bx + btnW && _my >= selY && _my <= selY + btnH;
                var   col = sel ? P.Gold : P.A(P.Muted, 0.5f);
                _r.Rect(bx, selY, btnW, btnH, (sel || hov) ? P.A(P.Gold, 0.18f) : P.A(P.Black, 0.4f));
                Border(bx, selY, btnW, btnH, (sel || hov) ? P.Gold : P.A(P.Muted, 0.3f), (sel || hov) ? 3 : 1);
                _r.TextC(i.ToString(), bx, selY, btnW, btnH, col, 24);
                int fi = i;
                _hits.Add(new HitRect(bx, selY, btnW, btnH, () => _g.PlayerSetupSel = fi));
            }

            // Sync player list size to selection for name display
            while (_g.Players.Count < _g.PlayerSetupSel) _g.Players.Add(new Player($"Player {_g.Players.Count + 1}"));
            while (_g.Players.Count > _g.PlayerSetupSel) _g.Players.RemoveAt(_g.Players.Count - 1);

            // Player name cards
            float cardW = 220, cardH = 90, cardGap = 16;
            float allW  = _g.PlayerSetupSel * cardW + (_g.PlayerSetupSel - 1) * cardGap;
            float cardX = (W - allW) / 2f;
            float cardY = selY + btnH + 30;

            for (int i = 0; i < _g.PlayerSetupSel; i++)
            {
                float bx  = cardX + i * (cardW + cardGap);
                var   col = PlayerColors.Get(i);
                bool  editing = _editingPlayer == i;
                bool  hov = !editing && _mx >= bx && _mx <= bx + cardW && _my >= cardY && _my <= cardY + cardH;

                _r.Rect(bx, cardY, cardW, cardH, editing ? P.A(col, 0.22f) : P.A(P.Black, 0.5f));
                Border(bx, cardY, cardW, cardH, editing ? col : (hov ? P.A(col, 0.7f) : P.A(col, 0.35f)), editing ? 3 : 2);

                // colour bar at top
                _r.Rect(bx + 2, cardY + 2, cardW - 4, 5, P.A(col, 0.7f));

                string nameDisp = editing
                    ? (_editBuf + ((int)(_now * 2) % 2 == 0 ? "|" : " "))
                    : _g.Players[i].Name;
                _r.TextC(nameDisp, bx, cardY + 16, cardW, _r.CapHeight(16) + 4,
                         editing ? P.White : P.A(P.White, 0.8f), 16);
                _r.TextC($"${_g.Players[i].Chips:N0} chips", bx, cardY + 52, cardW, _r.CapHeight(13),
                         P.A(col, 0.7f), 13);
                string hint = editing ? "Enter to confirm" : "Click to rename";
                _r.TextC(hint, bx, cardY + 70, cardW, _r.CapHeight(11), P.A(P.Muted, 0.5f), 11);

                int fi = i;
                _hits.Add(new HitRect(bx, cardY, cardW, cardH, () =>
                {
                    if (_editingPlayer == fi)
                    {
                        if (_editBuf.Trim().Length > 0) _g.Players[fi].Name = _editBuf.Trim();
                        _editingPlayer = -1; _editBuf = "";
                    }
                    else
                    {
                        _editingPlayer = fi;
                        _editBuf = _g.Players[fi].Name;
                    }
                }));
            }

            // Start button
            float startY = cardY + cardH + 28;
            float startW = 220, startH = 52;
            float startX = (W - startW) / 2f;
            bool  startHov = _mx >= startX && _mx <= startX + startW && _my >= startY && _my <= startY + startH;
            _r.Rect(startX, startY, startW, startH, startHov ? P.GreenDim : P.A(P.GreenDim, 0.5f));
            Border(startX, startY, startW, startH, startHov ? P.Green : P.A(P.Green, 0.5f), startHov ? 3 : 2);
            _r.TextC("START GAME", startX, startY, startW, startH, P.White, 20);
            _hits.Add(new HitRect(startX, startY, startW, startH, StartGame));

            // Back button
            float backW = 120, backH = 36;
            float backX = startX - backW - 16;
            Btn(backX, startY + (startH - backH) / 2f, backW, backH, "< BACK", P.Dim, () => _g.Phase = Phase.Menu, 14);
        }

        // ── menu ──────────────────────────────────────────────
        private void DrawMenu()
        {
            if (_prevPhase != Phase.Menu)
                _menuEnterTime = _now;
            _prevPhase = Phase.Menu;

            _r.Rect(0, 0, W, H - HUD_H, P.A(P.Black, 0.55f));

            float titY   = H * 0.18f;
            float titBob = (float)Math.Sin(_now * 1.1) * 3f;
            float titX   = (W - _r.Measure("BLACKJACK", 52)) / 2f;
            _r.Text("BLACKJACK", titX - 2, titY + titBob - 2, P.A(P.Gold, 0.18f), 52);
            _r.Text("BLACKJACK", titX + 2, titY + titBob + 2, P.A(P.Gold, 0.18f), 52);
            _r.Text("BLACKJACK", titX + 3, titY + titBob + 4, P.A(P.Black, 0.75f), 52);
            _r.Text("BLACKJACK", titX,     titY + titBob,     P.Gold, 52);

            float subY = titY + titBob + _r.CapHeight(52) + 12;
            ShadowText("6-Deck Shoe  ·  Vegas Rules",
                (W - _r.Measure("6-Deck Shoe  ·  Vegas Rules", 16)) / 2f,
                subY, P.A(P.Muted, 0.85f), 16, 1, 1);

            string[]  labels = { "PLAY", "BUY CHIPS", "STATS", "SLOTS", "QUIT" };
            string[]  subs   = { "Deal a new round", "Add more chips", "Session history", "Change save slot", "Exit the game" };
            Vector4[] cols   = { P.Green, P.Gold, P.Blue, P.Purple, P.Red };
            int       n      = labels.Length;

            float cw = 190, ch = 86, gap = 18;
            float totalW = n * cw + (n - 1) * gap;
            float sx     = (W - totalW) / 2f;
            float baseY  = H / 2f - ch / 2f;

            double waveT          = _now * 1.6;
            float  wavePhaseStep  = (float)(2 * Math.PI / n);
            float  dealDur        = 0.28f;
            float  dealStagger    = 0.13f;
            float  handTargetX    = W + 200, handTargetY = baseY + ch * 0.5f;

            for (int i = 0; i < n; i++)
            {
                float elapsed   = (float)(_now - _menuEnterTime) - i * dealStagger;
                float dealT     = Ease(Math.Clamp(elapsed / dealDur, 0f, 1f));
                float cardAlpha = dealT;
                float bx        = sx + i * (cw + gap);
                float slideY    = baseY + (H * 0.4f) * (1f - dealT);

                bool kbSel = _g.MenuSel == i;
                bool hov   = _mx >= bx && _mx <= bx + cw && _my >= slideY - 12 && _my <= slideY + ch + 12;
                bool act   = kbSel || hov;
                int  fi    = i;

                float bobAmt = act ? 7f : 4f;
                float bob    = dealT >= 1f ? (float)Math.Sin(waveT - i * wavePhaseStep) * bobAmt : 0f;
                float cy     = slideY + bob;

                if (elapsed >= 0f && elapsed <= dealDur) { handTargetX = bx + cw * 0.5f; handTargetY = cy + ch * 0.3f; }

                DrawMenuCard(bx, cy, cw, ch, cols[i], act, labels[i], subs[i], cardAlpha);
                _hits.Add(new HitRect(bx, cy - 12, cw, ch + 24, () => DoMenuSel(fi)));
            }

            DrawMenuDealerHand(handTargetX, handTargetY);
            CentreTextShadow("Arrow Keys + Enter  or  Click", baseY + ch + 28, P.A(P.Muted, 0.55f), 14);
        }

        private void DrawMenuDealerHand(float targetX, float targetY)
        {
            float startX     = W * 0.85f + 120;
            float startY     = H + 80;
            float lastCardEnd = (float)(_now - _menuEnterTime) - 4 * 0.13f;
            float retreatT   = lastCardEnd > 0.28f ? Ease(Math.Clamp((lastCardEnd - 0.28f) / 0.5f, 0f, 1f)) : 0f;

            float hx = targetX + (startX - targetX) * retreatT;
            float hy = targetY + (startY - targetY) * retreatT;

            float alpha = 1f - retreatT;
            if (alpha <= 0.01f) return;

            var skin   = P.A(new Vector4(0.95f, 0.78f, 0.60f, 1f), alpha);
            var sleeve = P.A(new Vector4(0.18f, 0.14f, 0.10f, 1f), alpha);
            var cuff   = P.A(new Vector4(0.90f, 0.88f, 0.82f, 1f), alpha);

            float sleeveW = 54, sleeveH = 120;
            _r.Rect(hx - sleeveW * 0.5f + 10, hy + 18, sleeveW, sleeveH, sleeve);
            _r.Rect(hx - sleeveW * 0.5f + 8,  hy + 14, sleeveW + 4, 14, cuff);

            float palmW = 46, palmH = 38;
            _r.Rect(hx - palmW * 0.5f, hy - palmH * 0.5f, palmW, palmH, skin);
            DrawDisc(hx - palmW * 0.5f - 6, hy, 10, skin);
            for (int f = 0; f < 4; f++)
                DrawDisc(hx - palmW * 0.5f + 6 + f * 11f, hy - palmH * 0.5f + 2, 7, skin);
        }

        // ── betting overlay ───────────────────────────────────
        private void DrawBettingOverlay()
        {
            var p  = _g.Players[_g.ActivePlayer];
            var col = PlayerColors.Get(_g.ActivePlayer);

            float cx = _g.Players.Count > 1 ? W / 2f : (W - SIDE_W) / 2f;
            float cy = (H - HUD_H) / 2f - 20;

            // Player turn banner for multiplayer
            if (_g.Players.Count > 1)
            {
                string banner = $"{p.Name}'s Turn";
                float  bw = _r.Measure(banner, 22) + 28;
                float  bh = _r.CapHeight(22) + 14;
                float  bx = (W - bw) / 2f, by = cy - bh - 18;
                _r.Rect(bx, by, bw, bh, P.A(col, 0.25f));
                Border(bx, by, bw, bh, P.A(col, 0.7f), 2);
                _r.TextC(banner, bx, by, bw, bh, col, 22);
            }

            string lbl = "CURRENT BET";
            string bs  = $"${p.BetAmount:N0}";
            float bsz  = 28f;

            float lw  = _r.Measure(lbl, 12);
            float bW  = _r.Measure(bs, bsz);
            float bch = _r.CapHeight(bsz);
            float lch = _r.CapHeight(12);

            float padX = 22f, padY = 14f, gapY = 8f;
            float boxW = Math.Max(lw, bW) + padX * 2;
            float boxH = lch + gapY + bch + padY * 2;
            float bbx  = cx - boxW / 2f;
            float bby  = cy - boxH / 2f;

            _r.Rect(bbx, bby, boxW, boxH, P.A(P.Black, 0.55f));
            Border(bbx, bby, boxW, boxH, P.A(col, 0.55f), 2);
            _r.Text(lbl, cx - lw / 2f, bby + padY, P.A(col, 0.75f), 12);
            _r.Text(bs,  cx - bW / 2f, bby + padY + lch + gapY, P.White, bsz);
        }

        // ── betting side panel ────────────────────────────────
        private void DrawBettingSidePanel()
        {
            var p   = _g.Players[_g.ActivePlayer];
            var col = PlayerColors.Get(_g.ActivePlayer);

            float px = W - SIDE_W;
            float py = 18;
            float ph = H - HUD_H - 18;

            _r.Rect(px, py, SIDE_W, ph, P.A(P.SidePanel, 0.97f));
            Border(px, py, SIDE_W, ph, P.A(col, 0.4f), 2);
            _r.Rect(px - 22, py + 60, 22, 60, P.A(P.SidePanel, 0.97f));
            Border(px - 22, py + 60, 22, 60, P.A(col, 0.4f), 2);
            _r.Rect(px, py + 60, 2, 60, P.A(P.SidePanel, 0.97f));
            float tlh = _r.CapHeight(12);
            _r.Text("BET", px - 18, py + 60 + (60 - tlh) / 2f, P.A(col, 0.6f), 12);

            float headerH = 40;
            _r.Rect(px, py, SIDE_W, headerH, P.A(col, 0.12f));
            string header = _g.Players.Count > 1 ? $"{p.Name} — BET" : "PLACE YOUR BET";
            _r.TextC(header, px, py, SIDE_W, headerH, col, 15);

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
                int   c2 = i % 2, row = i / 2;
                float cx2 = gridX + c2 * (chipD + chipGap);
                float cy2 = gridY + row * (chipD + chipGap);
                bool  hov = _mx >= cx2 && _mx <= cx2 + chipD && _my >= cy2 && _my <= cy2 + chipD;

                RoundRect(cx2, cy2, chipD, chipD, 10f, hov ? Brighten(cc, 0.25f) : cc);
                string vt = val >= 100 ? $"{val}" : $"${val}";
                float  vw = _r.Measure(vt, 14), vh = _r.CapHeight(14);
                _r.Text(vt, cx2 + (chipD - vw) / 2f, cy2 + (chipD - vh) / 2f, P.White, 14);

                int cap = val;
                _hits.Add(new HitRect(cx2, cy2, chipD, chipD, () =>
                {
                    p.BetAmount = Math.Min(p.Chips, p.BetAmount + cap);
                    _audio.Play("chip");
                }));
            }

            float btnRowY = gridY + 3 * (chipD + chipGap) + 4;
            float smBW = (SIDE_W - 40) / 2f, smBH = 34;
            Btn(px + 12,        btnRowY, smBW, smBH, "CLEAR", P.RedDim, () => p.BetAmount = 0, 14);
            Btn(px + 20 + smBW, btnRowY, smBW, smBH, "-$5",   P.Dim,    () => p.BetAmount = Math.Max(0, p.BetAmount - 5), 14);

            float dealY = btnRowY + smBH + 12;
            float dealH = 50;
            string dealLbl = _g.Players.Count > 1 && _g.ActivePlayer < _g.Players.Count - 1
                           ? "NEXT PLAYER →" : "DEAL";
            bool dealHov = _mx >= px + 12 && _mx <= px + SIDE_W - 12 && _my >= dealY && _my <= dealY + dealH;
            var  dealBg  = p.BetAmount > 0 && p.BetAmount <= p.Chips
                         ? (dealHov ? col : P.A(col, 0.4f))
                         : P.A(P.Dim, 0.4f);
            _r.Rect(px + 12, dealY, SIDE_W - 24, dealH, dealBg);
            Border(px + 12, dealY, SIDE_W - 24, dealH, dealHov ? P.White : P.A(col, 0.6f), 2);
            _r.TextC(dealLbl, px + 12, dealY, SIDE_W - 24, dealH, P.White, dealLbl.Length > 6 ? 16 : 22);
            if (p.BetAmount > 0 && p.BetAmount <= p.Chips)
                _hits.Add(new HitRect(px + 12, dealY, SIDE_W - 24, dealH, AdvanceBetting));

            float menuY = dealY + dealH + 10;
            Btn(px + 12, menuY, SIDE_W - 24, 34, "< MENU", P.Dim, () => _g.Phase = Phase.Menu, 14);
        }

        // ── cards ─────────────────────────────────────────────
        private void DrawCards()
        {
            float ty = 18, th = H - HUD_H - 18;
            float feltBottom = ty + th - RAIL_H;
            float divY    = ty + th * 0.46f;
            float playerH = feltBottom - divY - 10;

            // Dealer area
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

            // Player areas — one zone per player
            int   np    = _g.Players.Count;
            float pZoneX = 30f, pZoneW = W - 60f;
            float slotW  = pZoneW / Math.Max(np, 1);
            float pTop   = divY + 6;

            for (int pi = 0; pi < np; pi++)
            {
                var   player    = _g.Players[pi];
                var   pcol      = PlayerColors.Get(pi);
                bool  myTurn    = _g.Phase == Phase.PlayerTurn && pi == _g.ActivePlayer;
                float hzoneX    = pZoneX + pi * slotW;
                float hzoneW    = slotW - 6;

                // Player zone background when active
                if (myTurn)
                {
                    _r.Rect(hzoneX, pTop, hzoneW, playerH - 4, P.A(pcol, 0.07f));
                    Border(hzoneX, pTop, hzoneW, playerH - 4, P.A(pcol, 0.55f), 2);
                }
                else if (np > 1)
                {
                    Border(hzoneX, pTop, hzoneW, playerH - 4, P.A(pcol, 0.2f), 1);
                }

                float curY = pTop + 6;

                // Player name label
                string nameLbl = np > 1 ? player.Name : "YOUR HAND";
                _r.Text(nameLbl, hzoneX + 6, curY, myTurn ? pcol : P.A(pcol, 0.5f), 13);
                curY += _r.CapHeight(13) + 4;

                float pcW = 68, pcH = 96;
                int   hCount = player.Hands.Count;
                float handSlotW = hzoneW / Math.Max(hCount, 1);

                for (int hi = 0; hi < hCount; hi++)
                {
                    var  hand   = player.Hands[hi];
                    bool active = myTurn && hi == _g.ActiveHand;
                    float hx = hzoneX + hi * handSlotW;
                    float hw = handSlotW - 4;

                    if (active && hCount > 1)
                    {
                        _r.Rect(hx, curY - 2, hw, playerH - (curY - pTop) - 4, P.A(pcol, 0.12f));
                        Border(hx, curY - 2, hw, playerH - (curY - pTop) - 4, P.A(pcol, 0.5f), 2);
                    }

                    string betTxt = $"${hand.Bet}";
                    if (hand.IsDoubledDown) betTxt += " x2";
                    else if (hand.IsSplit)  betTxt += " spl";
                    float btW = _r.Measure(betTxt, 11) + 6, btH = _r.CapHeight(11) + 4;
                    _r.Rect(hx + hw - btW - 2, curY - 2, btW, btH, P.A(P.GoldDim, 0.8f));
                    _r.TextC(betTxt, hx + hw - btW - 2, curY - 2, btW, btH, P.Gold, 11);

                    if (!hand.Cards.Any(c => c.FaceDown))
                    {
                        string sc  = hand.ScoreText();
                        float  scW = _r.Measure(sc, 20) + 12, scH = _r.CapHeight(20) + 6;
                        var    scC = hand.IsBust()      ? P.Red
                                   : hand.IsBlackjack() ? P.A(P.Gold, 0.9f)
                                   : hand.Score() == 21 ? P.GreenDim
                                   : P.A(P.Black, 0.65f);
                        _r.Rect(hx + 4, curY, scW, scH, scC);
                        Border(hx + 4, curY, scW, scH, P.A(P.White, 0.12f), 1);
                        _r.TextC(sc, hx + 4, curY, scW, scH, P.White, 20);
                    }

                    float cardY = curY + _r.CapHeight(20) + 10;
                    DrawHandCards(hand.Cards, hx + 4, cardY, pcW, pcH);

                    float statusY = cardY + pcH + 4;
                    if (hand.IsBust())           _r.Text("BUST",       hx + 4, statusY, P.Red,  15);
                    else if (hand.IsBlackjack()) _r.Text("BLACKJACK!", hx + 4, statusY, P.Gold, 15);
                }
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
                float t     = Ease((float)(_now - c.DealTime) / dur);
                float offY  = c.IsDealer ? -(H * 0.45f) : (H * 0.45f);
                float cx    = destX;
                float cy    = y + offY * (1f - t);
                float alpha = t;

                if (c.FaceDown)
                {
                    _r.Rect(cx + 3, cy + 4, cw, ch, P.A(P.Black, 0.4f * alpha));
                    _r.Rect(cx, cy, cw, ch, P.A(P.CardBack, alpha));
                    for (int s = 0; s < 6; s++)
                        _r.Rect(cx + 4 + s * (cw / 6f), cy + 4, 3, ch - 8, P.A(P.CardBackStripe, 0.6f * alpha));
                    Border(cx, cy, cw, ch, P.A(P.White, 0.2f * alpha), 2);
                }
                else
                {
                    var fg = c.IsRed() ? P.CardRed : P.CardBlack;

                    _r.Rect(cx + 3, cy + 4, cw, ch, P.A(P.Black, 0.4f * alpha));
                    _r.Rect(cx, cy, cw, ch, P.A(P.CardFace, alpha));
                    Border(cx, cy, cw, ch, P.A(P.CardBorder, alpha), 1);

                    float rkSz = cw * 0.24f, pad = 4f;
                    string rank = c.RankGlyph();
                    _r.Text(rank, cx + pad, cy + pad, P.A(fg, alpha), rkSz);

                    float suitSzSmall = cw * 0.16f;
                    float suitY = cy + pad + _r.CapHeight(rkSz) + 2;
                    DrawSuit(c.Suit, cx + pad + _r.Measure(rank, rkSz) / 2f - suitSzSmall * 0.5f,
                             suitY, suitSzSmall, P.A(fg, alpha));

                    float suitSzBig = cw * 0.38f;
                    float midX = cx + cw / 2f, midY = cy + ch / 2f;
                    DrawSuit(c.Suit, midX - suitSzBig / 2f, midY - suitSzBig * 0.55f, suitSzBig, P.A(fg, alpha));

                    float cRkSz = cw * 0.22f;
                    float cRkW  = _r.Measure(rank, cRkSz);
                    _r.Text(rank, midX - cRkW / 2f, midY + suitSzBig * 0.5f + 2, P.A(fg, alpha * 0.7f), cRkSz);
                }
            }
        }

        // ── geometric suit drawing ─────────────────────────────
        // Suits are drawn using horizontal scanline strips for clean fills.
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

        // Scanline helper: fills horizontal span at each integer y row
        private void Scanline(float lx, float rx, float ry, float h, Vector4 col)
        {
            if (rx > lx) _r.Rect(lx, ry, rx - lx, h, col);
        }

        private void DrawHeart(float x, float y, float sz, Vector4 col)
        {
            // Parametric heart: x(t)=16sin³t, y(t)=13cos t-5cos2t-2cos3t-cos4t
            // Scaled to fit [x,x+sz] x [y,y+sz], sampled via scanlines.
            int rows = (int)Math.Ceiling(sz);
            for (int row = 0; row <= rows; row++)
            {
                float fy = row / (float)rows;
                float r  = sz * 0.28f;
                // Region 1: top two lobes (circular)
                if (row * (sz / rows) < r * 1.6f)
                {
                    float dy1 = row * (sz / rows) - r;
                    float xOff = dy1 < r ? (float)Math.Sqrt(Math.Max(0, r * r - dy1 * dy1)) : 0;
                    // left lobe centre: x + r
                    float llx = x + r - xOff;
                    float lrx = x + r + xOff;
                    // right lobe centre: x + sz - r
                    float rlx = x + sz - r - xOff;
                    float rrx = x + sz - r + xOff;
                    float spanL = Math.Min(llx, rlx);
                    float spanR = Math.Max(lrx, rrx);
                    Scanline(spanL, spanR, y + row * (sz / rows), sz / rows + 1, col);
                }
                else
                {
                    // Triangle region tapering to point at bottom
                    float progress = (row * (sz / rows) - r * 1.6f) / (sz - r * 1.6f);
                    float halfW    = (1f - progress) * sz * 0.5f;
                    float cx2      = x + sz * 0.5f;
                    Scanline(cx2 - halfW, cx2 + halfW, y + row * (sz / rows), sz / rows + 1, col);
                }
            }
        }

        private void DrawDiamond(float x, float y, float sz, Vector4 col)
        {
            float cx = x + sz / 2f, cy = y + sz / 2f;
            int rows = (int)Math.Ceiling(sz);
            for (int row = 0; row <= rows; row++)
            {
                float ry = y + row * (sz / rows);
                float t  = row / (float)rows;          // 0..1 top to bottom
                float halfW = t < 0.5f
                    ? t * 2f * sz * 0.5f               // expanding top half
                    : (1f - t) * 2f * sz * 0.5f;       // contracting bottom half
                Scanline(cx - halfW, cx + halfW, ry, sz / rows + 1, col);
            }
        }

        private void DrawClub(float x, float y, float sz, Vector4 col)
        {
            float cx  = x + sz / 2f;
            float r   = sz * 0.24f;
            int   rows = (int)Math.Ceiling(sz);
            // Three circles: top-centre, bottom-left, bottom-right
            float[] circX = { cx,       cx - r, cx + r };
            float[] circY = { y + r,    y + r * 2.6f, y + r * 2.6f };

            for (int row = 0; row <= rows; row++)
            {
                float ry   = y + row * (sz / rows);
                float bestL = float.MaxValue, bestR = float.MinValue;
                bool  hit  = false;
                foreach (var (ccx, ccy) in circX.Zip(circY))
                {
                    float dy = ry - ccy;
                    if (Math.Abs(dy) <= r)
                    {
                        float xOff = (float)Math.Sqrt(Math.Max(0, r * r - dy * dy));
                        bestL = Math.Min(bestL, ccx - xOff);
                        bestR = Math.Max(bestR, ccx + xOff);
                        hit = true;
                    }
                }
                if (hit) Scanline(bestL, bestR, ry, sz / rows + 1, col);

                // stem
                float stemW = sz * 0.14f, stemTop = y + sz * 0.68f;
                if (ry >= stemTop && ry <= y + sz * 0.88f)
                    Scanline(cx - stemW / 2f, cx + stemW / 2f, ry, sz / rows + 1, col);

                // foot
                float footTop = y + sz * 0.82f;
                if (ry >= footTop && ry <= y + sz)
                    Scanline(cx - sz * 0.22f, cx + sz * 0.22f, ry, sz / rows + 1, col);
            }
        }

        private void DrawSpade(float x, float y, float sz, Vector4 col)
        {
            float cx = x + sz / 2f;
            float r  = sz * 0.27f;
            int   rows = (int)Math.Ceiling(sz);

            for (int row = 0; row <= rows; row++)
            {
                float ry = y + row * (sz / rows);
                float rowF = row / (float)rows;

                // Top pointed triangle (upward)
                if (rowF <= 0.52f)
                {
                    float prog = rowF / 0.52f;       // 0 = very top, 1 = widest point
                    float halfW = prog * sz * 0.5f;
                    Scanline(cx - halfW, cx + halfW, ry, sz / rows + 1, col);
                }
                // Two lower lobes (circles)
                float lobeCY = y + sz * 0.52f + r;
                float dy = ry - lobeCY;
                if (Math.Abs(dy) <= r && rowF > 0.4f)
                {
                    float xOff = (float)Math.Sqrt(Math.Max(0, r * r - dy * dy));
                    Scanline(cx - r - xOff, cx - r + xOff, ry, sz / rows + 1, col); // left lobe
                    Scanline(cx + r - xOff, cx + r + xOff, ry, sz / rows + 1, col); // right lobe
                    // centre fill between lobes
                    Scanline(cx - r + xOff, cx + r - xOff, ry, sz / rows + 1, col);
                }

                // stem
                float stemW = sz * 0.14f, stemTop = y + sz * 0.70f;
                if (ry >= stemTop && ry <= y + sz * 0.88f)
                    Scanline(cx - stemW / 2f, cx + stemW / 2f, ry, sz / rows + 1, col);

                // foot
                float footTop = y + sz * 0.83f;
                if (ry >= footTop && ry <= y + sz)
                    Scanline(cx - sz * 0.22f, cx + sz * 0.22f, ry, sz / rows + 1, col);
            }
        }

        // ── action bar ────────────────────────────────────────
        private double LastDealTime()
        {
            double t = 0;
            foreach (var c in _g.Dealer.Cards) t = Math.Max(t, c.DealTime);
            foreach (var p in _g.Players) foreach (var h in p.Hands) foreach (var c in h.Cards) t = Math.Max(t, c.DealTime);
            return t;
        }

        private void DrawActionBar()
        {
            var hand = CurHand();
            if (hand == null) return;
            var p   = _g.Players[_g.ActivePlayer];
            var col = PlayerColors.Get(_g.ActivePlayer);

            float barDur = 0.18f, barDelay = 0.22f;
            float barT   = Ease((float)(_now - LastDealTime() - barDelay) / barDur);
            float slide  = (1f - barT) * (ACT_H + RAIL_H);
            float areaY  = H - HUD_H - RAIL_H - ACT_H + slide;

            _r.Rect(0, areaY, W, ACT_H + RAIL_H, P.A(P.Black, 0.72f * barT));
            _r.Rect(0, areaY, W, 2, P.A(col, 0.6f * barT));

            // Player name indicator in multiplayer
            if (_g.Players.Count > 1)
            {
                string pLbl = $"{p.Name}'s turn";
                float  pw = _r.Measure(pLbl, 13) + 16, ph = _r.CapHeight(13) + 8;
                _r.Rect(12, areaY + (ACT_H - ph) / 2f, pw, ph, P.A(col, 0.2f));
                Border(12, areaY + (ACT_H - ph) / 2f, pw, ph, P.A(col, 0.5f), 1);
                _r.TextC(pLbl, 12, areaY + (ACT_H - ph) / 2f, pw, ph, col, 13);
            }

            var acts = new (string lbl, string key, bool avail, Action act)[]
            {
                ("HIT",    "H", true,
                    () => { hand.AddCard(DealCard()); if (hand.IsBust()) NextHand(); }),
                ("STAND",  "S", true, NextHand),
                ("DOUBLE", "D", hand.Cards.Count == 2 && p.Chips >= hand.Bet, () => DoDouble(hand)),
                ("SPLIT",  "P", hand.CanSplit() && p.Hands.Count < 4 && p.Chips >= hand.Bet, () => DoSplit(hand)),
            };

            float bw = 160f, bh = 60f, gap = 14f;
            float totalBW = acts.Length * bw + (acts.Length - 1) * gap;
            float startX  = (W - totalBW) / 2f;
            float by      = areaY + (ACT_H - bh) / 2f;

            for (int i = 0; i < acts.Length; i++)
            {
                var (lbl, key, avail, act) = acts[i];
                float bx  = startX + i * (bw + gap);
                bool  hov = _mx >= bx && _mx <= bx + bw && _my >= by && _my <= by + bh;

                var bgC = !avail ? P.A(P.Dim, 0.25f) : hov ? col : P.A(col, 0.35f);
                var fgC = avail ? P.White : P.A(P.Muted, 0.4f);
                var bdr = !avail ? P.A(P.Dim, 0.2f) : hov ? P.A(P.White, 0.8f) : P.A(col, 0.5f);

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

            float cbW  = 210f, cbH = 52f;
            float cbX  = W - cbW - 30f;
            float cbY  = areaY + (ACT_H - cbH) / 2f;

            // Show results for each player, stacked or side by side
            int   np      = _g.Players.Count;
            float msgZoneW = cbX - 20f;
            float pSlotW   = msgZoneW / Math.Max(np, 1);

            for (int pi = 0; pi < np; pi++)
            {
                var p   = _g.Players[pi];
                var col = PlayerColors.Get(pi);
                float px = 20 + pi * pSlotW;

                if (np > 1)
                {
                    _r.Text(p.Name, px, areaY + 8, P.A(col, barT), 12);
                }

                float ry = areaY + (np > 1 ? 24 : 0);
                int   rn = Math.Min(p.Results.Count, np > 1 ? 2 : 3);
                for (int ri = 0; ri < rn; ri++)
                {
                    string msg = p.Results[ri];
                    var    mc  = msg.Contains("WIN") || msg.Contains("BLACKJACK") ? P.Green
                               : msg.Contains("BUST") || msg.Contains("LOSE")     ? P.Red
                               : msg.Contains("PUSH")                              ? P.Gold
                               : P.White;
                    _r.Text(msg, px, ry, P.A(mc, barT), np > 1 ? 13 : 16);
                    ry += _r.CapHeight(np > 1 ? 13 : 16) + 4;
                }
            }

            Btn(cbX, cbY, cbW, cbH, "CONTINUE →", P.Purple, () =>
            {
                if (_g.AutoPlay) StartNextRound();
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

            var p = _g.Players[0];
            double pct = p.HandsPlayed > 0 ? p.HandsWon * 100.0 / p.HandsPlayed : 0;
            (string k, string v, Vector4 vc)[] rows = {
                ("Hands Played",  p.HandsPlayed.ToString(), P.White),
                ("Hands Won",     p.HandsWon.ToString(),    P.Green),
                ("Win Rate",      $"{pct:F1}%",             P.Blue),
                ("Net Winnings",  $"{(p.NetWinnings >= 0 ? "+" : "")}${p.NetWinnings:N0}", p.NetWinnings >= 0 ? P.Green : P.Red),
                ("Current Chips", $"${p.Chips:N0}",         P.Gold),
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
        private void DrawMenuCard(float x, float y, float w, float h, Vector4 accent, bool active, string title, string sub, float alpha = 1f)
        {
            _r.Rect(x, y, w, h, active ? P.A(accent, 0.22f * alpha) : P.A(P.Black, 0.60f * alpha));
            Border(x, y, w, h, active ? P.A(accent, alpha) : P.A(accent, 0.4f * alpha), active ? 3 : 1);
            if (active) _r.Rect(x + 2, y + 2, w - 4, 3, P.A(accent, 0.35f * alpha));
            ShadowTextC(title, x, y,            w, h * 0.55f, P.A(active ? Brighten(accent, 0.15f) : P.Muted, active ? alpha : 0.9f * alpha), active ? 18 : 17);
            ShadowTextC(sub,   x, y + h * 0.5f, w, h * 0.5f, P.A(P.Muted, (active ? 0.85f : 0.60f) * alpha), 13);
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
            bool hov = _mx >= x && _mx <= x + w && _my >= y && _my <= y + h;
            _r.Rect(x, y, w, h, hov ? P.A(col, 0.25f) : P.A(P.Black, 0.4f));
            Border(x, y, w, h, hov ? col : P.A(col, 0.4f), hov ? 3 : 1);
            _r.TextC(lbl, x, y, w, h, hov ? P.White : P.A(P.White, 0.65f), sz);
            _hits.Add(new HitRect(x, y, w, h, onClick));
        }

        private void CentreText(string s, float y, Vector4 col, float sz)
        {
            _r.Text(s, (W - _r.Measure(s, sz)) / 2f, y, col, sz);
        }

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

        // ═══════════════════════════════════════════════════════════
        //  MODE SELECT
        // ═══════════════════════════════════════════════════════════
        private void DrawModeSelect()
        {
            _r.Rect(0, 0, W, H, P.A(P.Black, 0.78f));
            CentreText("SELECT MODE", H * 0.14f, P.Gold, 36);

            string[]  labels = { "BLACKJACK", "POKER",          "SLOTS"        };
            string[]  subs   = { "Classic 21", "Texas Hold'em", "Spin to win"  };
            Vector4[] cols   = { P.Green,       P.Blue,          P.Gold         };

            float cw = 220, ch = 120, gap = 30;
            float totalW = 3 * cw + 2 * gap;
            float sx = (W - totalW) / 2f;
            float cy = H / 2f - ch / 2f;

            for (int i = 0; i < 3; i++)
            {
                float bx  = sx + i * (cw + gap);
                bool  hov = _mx >= bx && _mx <= bx + cw && _my >= cy && _my <= cy + ch;
                int   fi  = i;
                _r.Rect(bx, cy, cw, ch, hov ? P.A(cols[i], 0.22f) : P.A(P.Black, 0.55f));
                Border(bx, cy, cw, ch, hov ? cols[i] : P.A(cols[i], 0.45f), hov ? 3 : 1);
                if (hov) _r.Rect(bx + 2, cy + 2, cw - 4, 3, P.A(cols[i], 0.4f));
                ShadowTextC(labels[i], bx, cy,           cw, ch * 0.6f, hov ? Brighten(cols[i], 0.15f) : P.A(cols[i], 0.8f), 20);
                ShadowTextC(subs[i],   bx, cy + ch * 0.55f, cw, ch * 0.45f, P.A(P.Muted, 0.7f), 14);
                _hits.Add(new HitRect(bx, cy, cw, ch, () =>
                {
                    switch (fi)
                    {
                        case 0: _g.Mode = GameMode.Blackjack; _g.Phase = Phase.PlayerSetup; break;
                        case 1: _g.Mode = GameMode.Poker;     _g.Phase = Phase.PokerSetup;  break;
                        case 2: _g.Mode = GameMode.Slots;     _g.Phase = Phase.SlotsPlay;
                                _slotsChips = _g.Players.Count > 0 ? _g.Players[0].Chips : 1000;
                                break;
                    }
                }));
            }
            Btn((W - 120) / 2f, cy + ch + 28, 120, 36, "< BACK", P.Dim, () => _g.Phase = Phase.Menu, 14);
        }

        // ═══════════════════════════════════════════════════════════
        //  POKER SETUP
        // ═══════════════════════════════════════════════════════════
        private void DrawPokerSetup()
        {
            _r.Rect(0, 0, W, H, P.A(P.Black, 0.78f));
            CentreText("POKER SETUP", H * 0.08f, P.Blue, 30);
            CentreText("Texas Hold'em", H * 0.08f + _r.CapHeight(30) + 8, P.A(P.Muted, 0.55f), 13);

            float y = H * 0.08f + _r.CapHeight(30) + 40;

            // ── Local vs LAN tabs ─────────────────────────────────
            float tabW = 160, tabH = 36, tabGap = 8;
            float tabsX = (W - (tabW * 2 + tabGap)) / 2f;
            string[] tabLabels = { "Local / vs AI", "LAN Multiplayer" };
            // _lanSetupMode: 0=local, 1=LAN
            if (!_lanSetupModeSet) _lanSetupMode = 0;
            for (int t = 0; t < 2; t++)
            {
                float tx2 = tabsX + t * (tabW + tabGap);
                bool  sel = _lanSetupMode == t;
                bool  hov = _mx >= tx2 && _mx <= tx2 + tabW && _my >= y && _my <= y + tabH;
                _r.Rect(tx2, y, tabW, tabH, sel ? P.A(P.Blue, 0.25f) : P.A(P.Black, 0.35f));
                Border(tx2, y, tabW, tabH, sel ? P.Blue : P.A(P.Muted, 0.3f), sel ? 3 : 1);
                _r.TextC(tabLabels[t], tx2, y, tabW, tabH, sel ? P.Blue : P.A(P.Muted, 0.6f), 14);
                int ft = t;
                _hits.Add(new HitRect(tx2, y, tabW, tabH, () => { _lanSetupMode = ft; _lanSetupModeSet = true; _lanStatusMsg = ""; }));
            }
            y += tabH + 18;

            if (_lanSetupMode == 0)
            {
                // ── Local mode ────────────────────────────────────
                CentreText("Human players:", y, P.A(P.Muted, 0.8f), 13);
                y += _r.CapHeight(13) + 8;

                float btnW = 54, btnH = 40, btnGap = 10;
                float totalBW = 4 * btnW + 3 * btnGap;
                float bsx = (W - totalBW) / 2f;
                for (int i = 1; i <= 4; i++)
                {
                    float bx  = bsx + (i - 1) * (btnW + btnGap);
                    bool  sel = _g.PlayerSetupSel == i;
                    bool  hov = _mx >= bx && _mx <= bx + btnW && _my >= y && _my <= y + btnH;
                    _r.Rect(bx, y, btnW, btnH, (sel || hov) ? P.A(P.Blue, 0.22f) : P.A(P.Black, 0.4f));
                    Border(bx, y, btnW, btnH, (sel || hov) ? P.Blue : P.A(P.Muted, 0.3f), (sel || hov) ? 3 : 1);
                    _r.TextC(i.ToString(), bx, y, btnW, btnH, (sel || hov) ? P.Blue : P.A(P.Muted, 0.6f), 18);
                    int fi = i;
                    _hits.Add(new HitRect(bx, y, btnW, btnH, () => _g.PlayerSetupSel = fi));
                }
                y += btnH + 16;

                while (_g.Players.Count < _g.PlayerSetupSel) _g.Players.Add(new Player($"Player {_g.Players.Count + 1}"));
                while (_g.Players.Count > _g.PlayerSetupSel) _g.Players.RemoveAt(_g.Players.Count - 1);

                float cardW = 170, cardH = 72, cardGap = 10;
                float allW  = _g.PlayerSetupSel * cardW + (_g.PlayerSetupSel - 1) * cardGap;
                float cardX = (W - allW) / 2f;

                for (int i = 0; i < _g.PlayerSetupSel; i++)
                {
                    float bx     = cardX + i * (cardW + cardGap);
                    bool  editing = _editingPlayer == i;
                    bool  hov    = !editing && _mx >= bx && _mx <= bx + cardW && _my >= y && _my <= y + cardH;
                    _r.Rect(bx, y, cardW, cardH, editing ? P.A(P.Blue, 0.2f) : P.A(P.Black, 0.45f));
                    Border(bx, y, cardW, cardH, editing ? P.Blue : (hov ? P.A(P.Blue, 0.7f) : P.A(P.Blue, 0.3f)), editing ? 3 : 2);
                    _r.Rect(bx + 2, y + 2, cardW - 4, 3, P.A(P.Blue, 0.6f));
                    string nd = editing ? (_editBuf + ((int)(_now * 2) % 2 == 0 ? "|" : " ")) : _g.Players[i].Name;
                    _r.TextC(nd, bx, y + 12, cardW, _r.CapHeight(14) + 4, editing ? P.White : P.A(P.White, 0.8f), 14);
                    _r.TextC(editing ? "Enter to confirm" : "Click to rename", bx, y + 52, cardW, _r.CapHeight(10), P.A(P.Muted, 0.5f), 10);
                    int fi = i;
                    _hits.Add(new HitRect(bx, y, cardW, cardH, () =>
                    {
                        if (_editingPlayer == fi) { if (_editBuf.Trim().Length > 0) _g.Players[fi].Name = _editBuf.Trim(); _editingPlayer = -1; _editBuf = ""; }
                        else { _editingPlayer = fi; _editBuf = _g.Players[fi].Name; }
                    }));
                }
                y += cardH + 20;

                Btn((W - 200) / 2f, y, 200, 46, "START POKER", P.Blue, StartPoker, 17);
            }
            else
            {
                // ── LAN mode ─────────────────────────────────────
                // Host section
                float panW = 320, panH = 120, panGap = 20;
                float panX = (W - (panW * 2 + panGap)) / 2f;

                // HOST panel
                _r.Rect(panX, y, panW, panH, P.A(P.Black, 0.4f));
                Border(panX, y, panW, panH, P.A(P.Green, 0.4f), 2);
                _r.TextC("HOST A GAME", panX, y + 8, panW, _r.CapHeight(15), P.Green, 15);
                CentreText($"Port: {LanHost.PORT}", y + 30, P.A(P.Muted, 0.6f), 11);

                // player count for LAN host
                float hbW = 36, hbH = 32, hbGap = 6;
                float hbsX = panX + (panW - (4 * hbW + 3 * hbGap)) / 2f;
                CentreText("seats (incl. you):", y + 44, P.A(P.Muted,0.6f), 11);
                for (int i = 2; i <= 5; i++)
                {
                    float bx  = hbsX + (i - 2) * (hbW + hbGap);
                    bool  sel = _lanHostSeats == i;
                    bool  hov = _mx >= bx && _mx <= bx + hbW && _my >= y+56 && _my <= y+56+hbH;
                    _r.Rect(bx, y+56, hbW, hbH, (sel||hov)?P.A(P.Green,0.2f):P.A(P.Black,0.3f));
                    Border(bx, y+56, hbW, hbH, (sel||hov)?P.Green:P.A(P.Muted,0.25f), (sel||hov)?2:1);
                    _r.TextC(i.ToString(), bx, y+56, hbW, hbH, (sel||hov)?P.Green:P.A(P.Muted,0.5f), 14);
                    int fi = i; _hits.Add(new HitRect(bx, y+56, hbW, hbH, () => _lanHostSeats = fi));
                }
                Btn(panX + (panW - 130) / 2f, y + panH - 36, 130, 30,
                    _isLanHost ? "HOSTING..." : "HOST", P.Green,
                    () => { if (!_isLanHost) StartLanHost(_lanHostSeats); }, 14);

                // JOIN panel
                float jpX = panX + panW + panGap;
                _r.Rect(jpX, y, panW, panH, P.A(P.Black, 0.4f));
                Border(jpX, y, panW, panH, P.A(P.Blue, 0.4f), 2);
                _r.TextC("JOIN A GAME", jpX, y + 8, panW, _r.CapHeight(15), P.Blue, 15);

                // IP input field
                bool editingIP = _editingPlayer == 99;
                float ipFW = panW - 24, ipFH = 28;
                float ipFX = jpX + 12, ipFY = y + 32;
                _r.Rect(ipFX, ipFY, ipFW, ipFH, editingIP ? P.A(P.Blue, 0.18f) : P.A(P.Black, 0.35f));
                Border(ipFX, ipFY, ipFW, ipFH, editingIP ? P.Blue : P.A(P.Muted, 0.3f), editingIP ? 2 : 1);
                string ipDisp = editingIP
                    ? (_editBuf + ((int)(_now * 2) % 2 == 0 ? "|" : " "))
                    : (_lanHostIP.Length > 0 ? _lanHostIP : "Enter host IP...");
                _r.TextC(ipDisp, ipFX, ipFY, ipFW, ipFH,
                    editingIP ? P.White : P.A(P.Muted, 0.5f), 13);
                _hits.Add(new HitRect(ipFX, ipFY, ipFW, ipFH, () =>
                {
                    if (editingIP) { _lanHostIP = _editBuf.Trim(); _editingPlayer = -1; _editBuf = ""; }
                    else           { _editingPlayer = 99; _editBuf = _lanHostIP; }
                }));

                string myName = _g.Players.Count > 0 ? _g.Players[0].Name : "Player";
                Btn(jpX + (panW - 130) / 2f, y + panH - 36, 130, 30,
                    _isLanClient ? "JOINING..." : "JOIN", P.Blue,
                    () => { if (!_isLanClient && _lanHostIP.Length > 0) StartLanClient(_lanHostIP, myName); }, 14);

                y += panH + 14;

                // Status message
                if (_lanStatusMsg.Length > 0)
                {
                    var sCol = _lanStatusMsg.StartsWith("Could") ? P.Red
                             : _lanConnected ? P.Green : P.Gold;
                    CentreText(_lanStatusMsg, y, sCol, 13);
                    y += _r.CapHeight(13) + 10;
                }

                // Start button (host only, once all connected)
                if (_isLanHost && _lanConnected)
                    Btn((W - 220) / 2f, y + 4, 220, 46, "START GAME", P.Green, StartPoker, 17);
            }

            Btn(30, H - 60, 100, 34, "< BACK", P.Dim, () => { CleanupLan(); _g.Phase = Phase.ModeSelect; }, 13);
        }

        // ═══════════════════════════════════════════════════════════
        //  POKER DRAW
        // ═══════════════════════════════════════════════════════════
        private void DrawPoker()
        {
            // Dark overlay on felt
            _r.Rect(0, 0, W, H - HUD_H, P.A(P.Black, 0.35f));

            bool showdown = _poker.Phase == PokerPhase.Showdown;
            int  np = _poker.Players.Count;

            // Community cards — centred
            float ccW = 72, ccH = 100;
            float ccGap = 8;
            float ccTotalW = 5 * ccW + 4 * ccGap;
            float ccX = (W - ccTotalW) / 2f;
            float ccY = (H - HUD_H) / 2f - ccH / 2f - 10;

            // Pot label above community
            string potLbl = $"POT: ${_poker.Pot}";
            float  potW   = _r.Measure(potLbl, 18) + 20, potH = _r.CapHeight(18) + 10;
            _r.Rect((W - potW) / 2f, ccY - potH - 8, potW, potH, P.A(P.Black, 0.55f));
            Border((W - potW) / 2f, ccY - potH - 8, potW, potH, P.A(P.Gold, 0.5f), 1);
            _r.TextC(potLbl, (W - potW) / 2f, ccY - potH - 8, potW, potH, P.Gold, 18);

            // Phase label — pulses brighter when community cards just dealt
            string phaseLbl = _poker.Phase switch {
                PokerPhase.Preflop  => "PRE-FLOP",
                PokerPhase.Flop     => "FLOP",
                PokerPhase.Turn     => "TURN",
                PokerPhase.River    => "RIVER",
                PokerPhase.Showdown => "SHOWDOWN",
                _ => ""
            };
            double dealAge = _now - (_poker.Community.Count > 0 ? _poker.Community[_poker.Community.Count - 1].DealTime : -999);
            float phaseAlpha = dealAge < 1.2 ? (float)(0.6 + 0.4 * Math.Max(0, 1.0 - dealAge / 1.2)) : 0.6f;
            var phaseCol = dealAge < 1.2 ? P.A(P.Gold, phaseAlpha) : P.A(P.Muted, 0.6f);
            float plW = _r.Measure(phaseLbl, 16) + 20, plH = _r.CapHeight(16) + 8;
            if (dealAge < 1.2 && _poker.Phase != PokerPhase.Preflop)
            {
                _r.Rect((W - plW) / 2f, ccY - potH - 42, plW, plH, P.A(P.Black, 0.6f));
                Border((W - plW) / 2f, ccY - potH - 42, plW, plH, P.A(P.Gold, phaseAlpha * 0.8f), 2);
            }
            _r.TextC(phaseLbl, (W - plW) / 2f, ccY - potH - 42, plW, plH, phaseCol, 16);

            // Draw 5 community card slots
            for (int i = 0; i < 5; i++)
            {
                float cx = ccX + i * (ccW + ccGap);
                if (i < _poker.Community.Count)
                    DrawPokerCard(_poker.Community[i], cx, ccY, ccW, ccH, 1f);
                else
                {
                    _r.Rect(cx, ccY, ccW, ccH, P.A(P.Black, 0.3f));
                    Border(cx, ccY, ccW, ccH, P.A(P.Muted, 0.2f), 1);
                }
            }

            // Player seats arranged in a semicircle around the table
            float seatW = 160, seatH = 110;
            var seats = PokerSeatPositions(np, W, H - HUD_H, seatW, seatH);

            for (int i = 0; i < np; i++)
            {
                var   p   = _poker.Players[i];
                var   col = p.IsHuman ? PlayerColors.Get(_poker.Players.Take(i+1).Count(x => x.IsHuman) - 1) : P.A(P.Muted, 0.6f);
                bool  act = i == _poker.ActiveIdx && _poker.Phase != PokerPhase.Showdown;
                bool  win = showdown && _poker.Winners.Contains(p);
                var   (sx, sy) = seats[i];

                var bg = p.IsFolded ? P.A(P.Black, 0.3f)
                       : win        ? P.A(P.Gold, 0.2f)
                       : act        ? P.A(col, 0.22f)
                       :              P.A(P.Black, 0.5f);
                var border = p.IsFolded ? P.A(P.Muted, 0.2f)
                           : win        ? P.Gold
                           : act        ? col
                           :              P.A(col, 0.35f);

                _r.Rect(sx, sy, seatW, seatH, bg);
                Border(sx, sy, seatW, seatH, border, act || win ? 3 : 1);
                if (act) _r.Rect(sx + 2, sy + 2, seatW - 4, 3, P.A(col, 0.5f));

                // Name + chips
                _r.TextC(p.Name, sx, sy + 6, seatW, _r.CapHeight(14), p.IsFolded ? P.A(P.Muted, 0.4f) : col, 14);
                _r.TextC($"${p.Chips}", sx, sy + 26, seatW, _r.CapHeight(13), P.A(P.Gold, p.IsFolded ? 0.3f : 0.8f), 13);

                // Last action badge
                if (p.LastAction != PokerAction.None && !win)
                {
                    string aLbl = p.LastActionLabel;
                    var    aCol = p.LastAction == PokerAction.Fold ? P.A(P.Red, 0.7f)
                                : p.LastAction == PokerAction.Raise || p.LastAction == PokerAction.AllIn ? P.A(P.Green, 0.8f)
                                : P.A(P.Muted, 0.6f);
                    float aw = _r.Measure(aLbl, 12) + 10, ah = _r.CapHeight(12) + 6;
                    _r.Rect(sx + (seatW - aw) / 2f, sy + 44, aw, ah, P.A(P.Black, 0.6f));
                    _r.TextC(aLbl, sx + (seatW - aw) / 2f, sy + 44, aw, ah, aCol, 12);
                }

                // Bet chip
                if (p.Bet > 0)
                {
                    string betS = $"${p.Bet}";
                    float  bw   = _r.Measure(betS, 12) + 8, bh = _r.CapHeight(12) + 6;
                    DrawDisc(sx + seatW / 2f, sy + seatH + 12, 16, P.A(P.ChipBlue, 0.85f));
                    _r.TextC(betS, sx + seatW / 2f - bw / 2f, sy + seatH + 5, bw, bh, P.White, 12);
                }

                // Hole cards (face down for AI unless showdown; face up for humans always)
                float hcW = 36, hcH = 52;
                float hcX = sx + (seatW - (2 * hcW + 4)) / 2f;
                float hcY = sy + seatH - hcH - 6;
                for (int ci = 0; ci < p.HoleCards.Count && ci < 2; ci++)
                {
                    bool reveal = p.IsHuman || showdown || p.IsFolded == false && win;
                    DrawPokerCard(p.HoleCards[ci], hcX + ci * (hcW + 4), hcY, hcW, hcH,
                                  p.IsFolded ? 0.35f : 1f, !reveal);
                }

                // Winner label
                if (win)
                {
                    string wlbl = "WINNER!";
                    float  ww   = _r.Measure(wlbl, 15) + 12, wh = _r.CapHeight(15) + 8;
                    _r.Rect(sx + (seatW - ww) / 2f, sy - wh - 4, ww, wh, P.A(P.Gold, 0.85f));
                    _r.TextC(wlbl, sx + (seatW - ww) / 2f, sy - wh - 4, ww, wh, P.Black, 15);
                }

                // Dealer button
                if (i == _poker.DealerIdx)
                {
                    DrawDisc(sx + seatW - 12, sy + 10, 10, P.White);
                    _r.TextC("D", sx + seatW - 22, sy + 2, 20, 18, P.Black, 11);
                }
            }

            // Action bar for human turn
            if (_poker.IsHumanTurn && _poker.Phase != PokerPhase.Showdown)
                DrawPokerActionBar();
            else if (showdown)
                DrawPokerShowdownBar();

            // Showdown message
            if (showdown && _poker.ShowdownMsg != "")
            {
                float mw = Math.Min(_r.Measure(_poker.ShowdownMsg, 20) + 24, W - 60);
                float mh = _r.CapHeight(20) + 14;
                float mx = (W - mw) / 2f, my = ccY + ccH + 16;
                _r.Rect(mx, my, mw, mh, P.A(P.Black, 0.75f));
                Border(mx, my, mw, mh, P.A(P.Gold, 0.7f), 2);
                _r.TextC(_poker.ShowdownMsg, mx, my, mw, mh, P.Gold, 20);
            }
        }

        private void DrawPokerActionBar()
        {
            float bh = 56, gap = 10;
            float areaH = bh + 28;
            float areaY = H - HUD_H - areaH;
            _r.Rect(0, areaY, W, areaH, P.A(P.Black, 0.78f));
            _r.Rect(0, areaY, W, 2, P.A(P.Blue, 0.7f));

            var p      = _poker.ActivePlayer!;
            int toCall = _poker.CurrentBet - p.Bet;
            bool canCheck = toCall == 0;

            string callLbl = canCheck ? "CHECK (C)" : $"CALL ${toCall} (C)";
            string raiseLbl = $"RAISE +${_poker.RaiseAmount} (R)";

            var btns = new (string lbl, Vector4 col, Action act)[] {
                ("FOLD (F)",  P.Red,   () => DoPokerHumanAction(PokerAction.Fold)),
                (callLbl,     P.Green, () => DoPokerHumanAction(canCheck ? PokerAction.Check : PokerAction.Call)),
                (raiseLbl,    P.Blue,  () => DoPokerHumanAction(PokerAction.Raise)),
                ("ALL IN (A)",P.Gold,  () => DoPokerHumanAction(PokerAction.AllIn)),
            };

            float totalW  = btns.Length * 180 + (btns.Length - 1) * gap;
            float startX  = (W - totalW) / 2f;
            float by      = areaY + (areaH - bh) / 2f;

            for (int i = 0; i < btns.Length; i++)
            {
                var (lbl, col, act) = btns[i];
                float bx  = startX + i * (180 + gap);
                bool  hov = _mx >= bx && _mx <= bx + 180 && _my >= by && _my <= by + bh;
                _r.Rect(bx, by, 180, bh, hov ? P.A(col, 0.3f) : P.A(P.Black, 0.5f));
                Border(bx, by, 180, bh, hov ? col : P.A(col, 0.45f), hov ? 3 : 1);
                _r.TextC(lbl, bx, by, 180, bh, hov ? P.White : P.A(P.White, 0.7f), 15);
                var ca = act;
                _hits.Add(new HitRect(bx, by, 180, bh, ca));
            }

            // Raise size adjuster
            float raiseBtnX = startX + 2 * (180 + gap);
            float adjY = by + bh + 4;
            float adjBW = 28, adjBH = 18;
            Btn(raiseBtnX,          adjY, adjBW, adjBH, "-", P.Dim, () => _poker.RaiseAmount = Math.Max(_poker.BigBlind, _poker.RaiseAmount - _poker.BigBlind), 13);
            Btn(raiseBtnX + 180 - adjBW, adjY, adjBW, adjBH, "+", P.Dim, () => _poker.RaiseAmount = Math.Min(p.Chips, _poker.RaiseAmount + _poker.BigBlind), 13);
        }

        private void DrawPokerShowdownBar()
        {
            float bh = 52, areaH = bh + 24;
            float areaY = H - HUD_H - areaH;
            _r.Rect(0, areaY, W, areaH, P.A(P.Black, 0.78f));
            _r.Rect(0, areaY, W, 2, P.A(P.Gold, 0.7f));
            float bw = 220;
            bool anyHuman = _poker.Players.Any(p => p.IsHuman && p.Chips > 0);
            string lbl = anyHuman ? "NEXT HAND →" : "BACK TO MENU";
            Btn((W - bw) / 2f, areaY + (areaH - bh) / 2f, bw, bh, lbl, P.Purple, PokerNextHand, 18);
        }

        private void DrawPokerCard(Card c, float x, float y, float cw, float ch, float alpha, bool faceDown = false)
        {
            // Deal animation: slide from deck position (centre of table)
            float drawX = x, drawY = y;
            float deckX = W / 2f - cw / 2f, deckY = (H - HUD_H) / 2f - ch / 2f;
            if (c.DealTime > 0 && _now < c.DealTime + 0.45)
            {
                float t = Ease(Math.Clamp((float)(_now - c.DealTime) / 0.38f, 0f, 1f));
                drawX = deckX + (x - deckX) * t;
                drawY = deckY + (y - deckY) * t;
                alpha *= t;
            }
            else if (c.DealTime > 0 && _now < c.DealTime)
            {
                // Not yet dealt — invisible
                return;
            }

            if (faceDown)
            {
                _r.Rect(drawX + 2, drawY + 3, cw, ch, P.A(P.Black, 0.35f * alpha));
                _r.Rect(drawX, drawY, cw, ch, P.A(P.CardBack, alpha));
                Border(drawX, drawY, cw, ch, P.A(P.White, 0.18f * alpha), 1);
                return;
            }
            var fg = c.IsRed() ? P.CardRed : P.CardBlack;
            _r.Rect(drawX + 2, drawY + 3, cw, ch, P.A(P.Black, 0.35f * alpha));
            _r.Rect(drawX, drawY, cw, ch, P.A(P.CardFace, alpha));
            Border(drawX, drawY, cw, ch, P.A(P.CardBorder, alpha), 1);
            float rkSz = cw * 0.28f, pad = 3f;
            string rank = c.RankGlyph();
            _r.Text(rank, drawX + pad, drawY + pad, P.A(fg, alpha), rkSz);
            float sSz = cw * 0.22f, sY2 = drawY + pad + _r.CapHeight(rkSz) + 1;
            DrawSuit(c.Suit, drawX + pad + _r.Measure(rank, rkSz) / 2f - sSz * 0.5f, sY2, sSz, P.A(fg, alpha));
            float bigSz = cw * 0.42f;
            DrawSuit(c.Suit, drawX + cw / 2f - bigSz / 2f, drawY + ch / 2f - bigSz * 0.52f, bigSz, P.A(fg, alpha));
        }

        private static (float x, float y)[] PokerSeatPositions(int n, float W, float H, float sw, float sh)
        {
            var seats = new (float, float)[n];
            // Spread players evenly: bottom arc for humans, top arc for AI
            float cx = W / 2f, cy = (H - 44) / 2f;
            float rx = W * 0.42f, ry = (H - 44) * 0.40f;
            for (int i = 0; i < n; i++)
            {
                // Start at bottom-centre, go clockwise
                double angle = Math.PI / 2f + i * (2 * Math.PI / n);
                float  sx    = cx + rx * (float)Math.Cos(angle) - sw / 2f;
                float  sy    = cy + ry * (float)Math.Sin(angle) - sh / 2f;
                // Clamp to screen
                sx = Math.Clamp(sx, 10, W - sw - 10);
                sy = Math.Clamp(sy, 10, H - sh - 60);
                seats[i] = (sx, sy);
            }
            return seats;
        }

        // ═══════════════════════════════════════════════════════════
        //  SLOTS DRAW
        // ═══════════════════════════════════════════════════════════
        private void DrawSlots()
        {
            _r.Rect(0, 0, W, H - HUD_H, P.A(P.Black, 0.55f));

            // Machine frame
            float mw = 560, mh = 440;
            float mx = (W - mw) / 2f, my = (H - HUD_H - mh) / 2f;
            _r.Rect(mx, my, mw, mh, new Vector4(0.18f, 0.12f, 0.06f, 1f));
            Border(mx, my, mw, mh, P.A(P.Gold, 0.7f), 4);
            _r.Rect(mx + 4, my + 4, mw - 8, 4, P.A(P.GoldBright, 0.5f));

            // Title
            ShadowTextC("LUCKY SEVENS", mx, my + 8, mw, _r.CapHeight(22) + 6, P.Gold, 22);

            // Reel window
            float rw = mw - 80, rh = 200;
            float rx2 = mx + 40, ry = my + 60;
            _r.Rect(rx2, ry, rw, rh, P.A(P.Black, 0.8f));
            Border(rx2, ry, rw, rh, P.A(P.Gold, 0.5f), 3);

            // Payline indicator
            float lineY = ry + rh / 2f - 2;
            _r.Rect(rx2 - 6, lineY, 6, 4, P.Red);
            _r.Rect(rx2 + rw, lineY, 6, 4, P.Red);

            float cellW = rw / SlotsGame.REELS;
            float cellH = rh / SlotsGame.ROWS;

            for (int reel = 0; reel < SlotsGame.REELS; reel++)
            {
                float cx2 = rx2 + reel * cellW;
                // Vertical dividers
                if (reel > 0) _r.Rect(cx2, ry + 8, 2, rh - 16, P.A(P.Gold, 0.3f));

                for (int row = 0; row < SlotsGame.ROWS; row++)
                {
                    float cy2  = ry + row * cellH;
                    var   sym  = _slots.Display[reel, row];
                    bool  payline = row == 1;
                    float alpha = payline ? 1f : 0.45f;
                    DrawSlotSymbol(sym, cx2 + cellW / 2f, cy2 + cellH / 2f, cellH * 0.55f, alpha);
                }
            }

            // Payline highlight when win
            if (_slots.ShowResult && _slots.LastWin > 0)
            {
                _r.Rect(rx2, lineY - 1, rw, 6, P.A(P.Gold, (float)(0.5 + 0.4 * Math.Sin(_now * 8))));
                string wlbl = _slots.WinLabel + $"  +${_slots.LastWin}";
                float  ww   = _r.Measure(wlbl, 22) + 20, wh = _r.CapHeight(22) + 12;
                _r.Rect((W - ww) / 2f, ry + rh + 10, ww, wh, P.A(P.Black, 0.7f));
                Border((W - ww) / 2f, ry + rh + 10, ww, wh, P.A(P.Gold, 0.8f), 2);
                _r.TextC(wlbl, (W - ww) / 2f, ry + rh + 10, ww, wh, P.Gold, 22);
            }

            // Bet controls
            float ctrlY = ry + rh + 50;
            string betLbl = $"BET: ${_slots.Bet}";
            float  bw2    = _r.Measure(betLbl, 18) + 60;
            float  bx2    = (W - bw2) / 2f;
            Btn(bx2, ctrlY, 32, 32, "-", P.Dim, () => _slots.Bet = Math.Max(1, _slots.Bet - 1), 16);
            _r.TextC(betLbl, bx2 + 32, ctrlY, bw2 - 64, 32, P.Gold, 18);
            Btn(bx2 + bw2 - 32, ctrlY, 32, 32, "+", P.Dim, () => _slots.Bet = Math.Min(Math.Min(100, _slotsChips), _slots.Bet + 1), 16);

            // Spin button
            float sbW = 180, sbH = 54;
            float sbX = (W - sbW) / 2f, sbY = ctrlY + 42;
            bool  canSpin = !_slots.IsSpinning && _slots.Bet <= _slotsChips && _slots.Bet > 0;
            bool  sbHov   = _mx >= sbX && _mx <= sbX + sbW && _my >= sbY && _my <= sbY + sbH;
            var   sbCol   = canSpin ? (sbHov ? P.Green : P.GreenDim) : P.A(P.Dim, 0.4f);
            _r.Rect(sbX, sbY, sbW, sbH, sbCol);
            Border(sbX, sbY, sbW, sbH, sbHov && canSpin ? P.White : P.A(P.Green, 0.5f), sbHov ? 3 : 2);
            _r.TextC(_slots.IsSpinning ? "SPINNING..." : "SPIN  (Space)", sbX, sbY, sbW, sbH, P.White, 17);
            if (canSpin)
                _hits.Add(new HitRect(sbX, sbY, sbW, sbH, () => { _slotsChips -= _slots.Bet; _slots.Spin(_slotsChips + _slots.Bet); }));

            // Chips display
            string chipsLbl = $"CREDITS: ${_slotsChips}";
            float  cw2 = _r.Measure(chipsLbl, 16) + 16, ch2 = _r.CapHeight(16) + 8;
            _r.Rect(mx + 20, my + mh - ch2 - 12, cw2, ch2, P.A(P.Black, 0.5f));
            _r.TextC(chipsLbl, mx + 20, my + mh - ch2 - 12, cw2, ch2, P.Gold, 16);

            // Pay table button
            Btn(mx + mw - 110, my + mh - 38, 100, 28, "PAY TABLE", P.Dim, () => { }, 12);

            // Back button
            Btn(mx + 10, my + mh - 38, 90, 28, "< BACK", P.Dim, () =>
            {
                if (_g.Players.Count > 0) _g.Players[0].Chips = _slotsChips;
                _g.Phase = Phase.ModeSelect;
            }, 12);
        }

        private void DrawSlotSymbol(SlotSymbol sym, float cx, float cy, float sz, float alpha)
        {
            var col  = P.A(SlotSymbolColor(sym), alpha);
            var dark = P.A(Brighten(SlotSymbolColor(sym), -0.25f), alpha);
            var lite = P.A(Brighten(SlotSymbolColor(sym),  0.35f), alpha);
            var whi  = P.A(P.White, alpha * 0.55f);

            switch (sym)
            {
                case SlotSymbol.Cherry:
                {
                    // Stem — thin green line up then branches
                    var green     = P.A(new Vector4(0.15f, 0.65f, 0.1f,  1f), alpha);
                    var greenDark = P.A(new Vector4(0.08f, 0.40f, 0.05f, 1f), alpha);
                    // Main stem
                    _r.Rect(cx - 2,          cy - sz * 0.55f, 4,  sz * 0.42f, green);
                    // Left branch
                    _r.Rect(cx - sz * 0.22f, cy - sz * 0.28f, sz * 0.22f, 3, green);
                    // Right branch
                    _r.Rect(cx + 2,          cy - sz * 0.20f, sz * 0.20f, 3, green);
                    // Left leaf
                    _r.Rect(cx - sz * 0.38f, cy - sz * 0.36f, sz * 0.18f, sz * 0.12f, green);
                    _r.Rect(cx - sz * 0.34f, cy - sz * 0.34f, sz * 0.10f, sz * 0.06f, greenDark);
                    // Left cherry body
                    float lcx = cx - sz * 0.22f, lcy = cy + sz * 0.10f, cr = sz * 0.26f;
                    DrawDisc(lcx, lcy, cr, P.A(new Vector4(0.70f, 0.05f, 0.05f, 1f), alpha));
                    DrawDisc(lcx, lcy, cr * 0.85f, col);
                    DrawDisc(lcx - cr * 0.25f, lcy - cr * 0.25f, cr * 0.22f, whi); // shine
                    // Right cherry body
                    float rcx = cx + sz * 0.18f, rcy = cy + sz * 0.18f;
                    DrawDisc(rcx, rcy, cr, P.A(new Vector4(0.70f, 0.05f, 0.05f, 1f), alpha));
                    DrawDisc(rcx, rcy, cr * 0.85f, col);
                    DrawDisc(rcx - cr * 0.25f, rcy - cr * 0.25f, cr * 0.22f, whi);
                    break;
                }
                case SlotSymbol.Lemon:
                {
                    // Oval lemon body
                    int steps = 18;
                    for (int i = 0; i < steps; i++)
                    {
                        float t  = (float)i / steps;
                        float ya = cy - sz * 0.38f + t * sz * 0.76f;
                        float hw = sz * 0.30f * (float)Math.Sin(t * Math.PI);
                        _r.Rect(cx - hw, ya, hw * 2f, sz * 0.76f / steps + 1, i < steps / 2 ? col : dark);
                    }
                    // Nub tip top
                    _r.Rect(cx - sz * 0.06f, cy - sz * 0.46f, sz * 0.12f, sz * 0.10f, col);
                    // Nub tip bottom
                    _r.Rect(cx - sz * 0.06f, cy + sz * 0.36f, sz * 0.12f, sz * 0.10f, dark);
                    // Shine
                    DrawDisc(cx - sz * 0.10f, cy - sz * 0.18f, sz * 0.09f, whi);
                    break;
                }
                case SlotSymbol.Orange:
                {
                    // Round orange body
                    float r2 = sz * 0.34f;
                    int   steps = 20;
                    for (int i = 0; i < steps; i++)
                    {
                        float t  = (float)i / steps;
                        float ya = cy - r2 + t * r2 * 2f;
                        float hw = r2 * (float)Math.Sin(t * Math.PI);
                        _r.Rect(cx - hw, ya, hw * 2f, r2 * 2f / steps + 1, i < steps * 0.55f ? col : dark);
                    }
                    // Leaf/stem
                    var green = P.A(new Vector4(0.15f, 0.60f, 0.08f, 1f), alpha);
                    _r.Rect(cx - 2,          cy - r2 - sz * 0.14f, 4,  sz * 0.14f, green);
                    _r.Rect(cx - sz * 0.14f, cy - r2 - sz * 0.06f, sz * 0.14f, sz * 0.08f, green);
                    // Shine + navel dot
                    DrawDisc(cx - r2 * 0.30f, cy - r2 * 0.30f, sz * 0.09f, whi);
                    DrawDisc(cx, cy + r2 * 0.65f, sz * 0.045f, dark);
                    break;
                }
                case SlotSymbol.Plum:
                {
                    float r2 = sz * 0.30f;
                    int   steps = 18;
                    for (int i = 0; i < steps; i++)
                    {
                        float t  = (float)i / steps;
                        float ya = cy - r2 * 0.7f + t * r2 * 1.8f;
                        float hw = r2 * (float)Math.Sin(t * Math.PI);
                        _r.Rect(cx - hw, ya, hw * 2f, r2 * 1.8f / steps + 1, i < steps * 0.5f ? col : dark);
                    }
                    // Crease line
                    _r.Rect(cx - 2, cy - r2 * 0.6f, 3, r2 * 1.5f, dark);
                    // Stem
                    var green = P.A(new Vector4(0.2f, 0.55f, 0.08f, 1f), alpha);
                    _r.Rect(cx - 2, cy - r2 * 0.8f - sz * 0.22f, 4, sz * 0.22f, green);
                    // Leaf
                    _r.Rect(cx + 2, cy - r2 * 0.8f - sz * 0.14f, sz * 0.16f, sz * 0.08f, green);
                    // Shine
                    DrawDisc(cx - r2 * 0.30f, cy - r2 * 0.20f, sz * 0.08f, whi);
                    break;
                }
                case SlotSymbol.Bell:
                {
                    // Bell dome — wide at bottom, narrow at top
                    int steps = 22;
                    float bellH = sz * 0.58f;
                    float bellTopY = cy - sz * 0.42f;
                    for (int i = 0; i < steps; i++)
                    {
                        float t  = (float)i / steps;
                        float ya = bellTopY + t * bellH;
                        // Eased width profile
                        float wt = (float)(1.0 - Math.Pow(1.0 - t, 2.0));
                        float hw = sz * 0.36f * wt;
                        _r.Rect(cx - hw, ya, hw * 2f, bellH / steps + 1,
                            t < 0.4f ? lite : (t < 0.7f ? col : dark));
                    }
                    // Bell base bar
                    _r.Rect(cx - sz * 0.38f, cy + sz * 0.16f, sz * 0.76f, sz * 0.10f, dark);
                    // Top knob
                    _r.Rect(cx - sz * 0.06f, cy - sz * 0.50f, sz * 0.12f, sz * 0.10f, dark);
                    // Clapper
                    DrawDisc(cx, cy + sz * 0.32f, sz * 0.09f, P.A(Brighten(SlotSymbolColor(sym), -0.4f), alpha));
                    DrawDisc(cx, cy + sz * 0.32f, sz * 0.06f, P.A(new Vector4(0.6f, 0.5f, 0.1f, 1f), alpha));
                    // Shine
                    DrawDisc(cx - sz * 0.14f, cy - sz * 0.22f, sz * 0.07f, whi);
                    break;
                }
                case SlotSymbol.Bar:
                {
                    // Three stacked bars in gold/brown
                    var barCol   = col;
                    var barLite  = lite;
                    var barDark  = dark;
                    // Bottom bar
                    _r.Rect(cx - sz * 0.36f, cy + sz * 0.10f, sz * 0.72f, sz * 0.20f, barDark);
                    _r.Rect(cx - sz * 0.34f, cy + sz * 0.11f, sz * 0.68f, sz * 0.06f, barLite);
                    // Middle bar (wider)
                    _r.Rect(cx - sz * 0.40f, cy - sz * 0.08f, sz * 0.80f, sz * 0.18f, barCol);
                    _r.Rect(cx - sz * 0.38f, cy - sz * 0.07f, sz * 0.76f, sz * 0.05f, barLite);
                    // Top bar
                    _r.Rect(cx - sz * 0.32f, cy - sz * 0.26f, sz * 0.64f, sz * 0.18f, barDark);
                    _r.Rect(cx - sz * 0.30f, cy - sz * 0.25f, sz * 0.60f, sz * 0.05f, barLite);
                    // "BAR" text overlay
                    float tw = _r.Measure("BAR", 11);
                    _r.TextC("BAR", cx - sz * 0.40f, cy - sz * 0.08f, sz * 0.80f, sz * 0.18f,
                        P.A(new Vector4(1f, 0.95f, 0.7f, 1f), alpha), 11);
                    break;
                }
                case SlotSymbol.Seven:
                {
                    // Outline (black shadow)
                    var outline = P.A(new Vector4(0.08f, 0f, 0f, 1f), alpha * 0.7f);
                    int d = 2;
                    // Top horizontal
                    _r.Rect(cx - sz*0.26f - d, cy - sz*0.40f - d, sz*0.52f + d*2, sz*0.13f + d*2, outline);
                    // Right vertical top half
                    _r.Rect(cx + sz*0.13f - d, cy - sz*0.40f - d, sz*0.15f + d*2, sz*0.42f + d*2, outline);
                    // Diagonal slash
                    for (int si = 0; si < 12; si++)
                    {
                        float t = si / 12f;
                        float sx2 = cx + sz*(0.13f - t*0.52f) - d;
                        float sy2 = cy + sz*(0.02f + t*0.38f) - d;
                        _r.Rect(sx2, sy2, sz*0.16f + d*2, sz*0.38f/12f + d*2, outline);
                    }
                    // Top horizontal fill
                    _r.Rect(cx - sz*0.26f, cy - sz*0.40f, sz*0.52f, sz*0.13f, col);
                    _r.Rect(cx - sz*0.24f, cy - sz*0.39f, sz*0.48f, sz*0.05f, lite);
                    // Right vertical
                    _r.Rect(cx + sz*0.13f, cy - sz*0.40f, sz*0.15f, sz*0.42f, col);
                    // Diagonal body
                    for (int si = 0; si < 12; si++)
                    {
                        float t  = si / 12f;
                        float sx2 = cx + sz*(0.13f - t*0.52f);
                        float sy2 = cy + sz*(0.02f + t*0.38f);
                        _r.Rect(sx2, sy2, sz*0.16f, sz*0.38f/12f + 1, si < 6 ? col : dark);
                    }
                    break;
                }
                case SlotSymbol.Diamond:
                {
                    // Multi-facet diamond
                    float dw = sz * 0.62f, dh = sz * 0.72f;
                    float dtx = cx, dty = cy - dh * 0.5f; // top point
                    float dbx = cx, dby = cy + dh * 0.5f; // bottom point
                    float dlx = cx - dw * 0.5f, dly = cy - dh * 0.05f; // left
                    float drx = cx + dw * 0.5f, dry = cy - dh * 0.05f; // right
                    float dmly = cy - dh * 0.12f; // mid-belt y

                    // Draw as horizontal scanlines
                    int scanLines = (int)(dh) + 2;
                    for (int si = 0; si < scanLines; si++)
                    {
                        float t  = (float)si / scanLines;
                        float yy = dty + t * dh;
                        // Upper half
                        float hw;
                        if (yy <= dly)
                        {
                            float tt = (yy - dty) / (dly - dty);
                            hw = dw * 0.5f * tt;
                        }
                        else
                        {
                            float tt = (yy - dly) / (dby - dly);
                            hw = dw * 0.5f * (1f - tt);
                        }
                        hw = Math.Max(0, hw);
                        Vector4 fc;
                        if (t < 0.15f)      fc = lite;
                        else if (t < 0.30f) fc = P.A(new Vector4(0.80f, 0.95f, 1.00f, 1f), alpha);
                        else if (t < 0.50f) fc = col;
                        else if (t < 0.65f) fc = P.A(new Vector4(0.35f, 0.70f, 0.95f, 1f), alpha);
                        else if (t < 0.80f) fc = dark;
                        else                fc = P.A(new Vector4(0.20f, 0.55f, 0.85f, 1f), alpha);
                        _r.Rect(cx - hw, yy, hw * 2f, dh / scanLines + 1, fc);
                    }
                    // Horizontal belt crease
                    _r.Rect(cx - dw * 0.50f, dly - 1, dw, 2, P.A(P.White, alpha * 0.6f));
                    // Top shine
                    DrawDisc(cx, dty + dh * 0.10f, sz * 0.07f, P.A(P.White, alpha * 0.8f));
                    break;
                }
            }
        }

        private static Vector4 SlotSymbolColor(SlotSymbol s) => s switch {
            SlotSymbol.Cherry  => new Vector4(0.90f, 0.10f, 0.15f, 1f),
            SlotSymbol.Lemon   => new Vector4(1.00f, 0.90f, 0.10f, 1f),
            SlotSymbol.Orange  => new Vector4(1.00f, 0.55f, 0.05f, 1f),
            SlotSymbol.Plum    => new Vector4(0.55f, 0.10f, 0.65f, 1f),
            SlotSymbol.Bell    => new Vector4(1.00f, 0.82f, 0.20f, 1f),
            SlotSymbol.Bar     => new Vector4(0.30f, 0.25f, 0.20f, 1f),
            SlotSymbol.Seven   => new Vector4(0.90f, 0.10f, 0.15f, 1f),
            SlotSymbol.Diamond => new Vector4(0.55f, 0.85f, 1.00f, 1f),
            _ => Vector4.One
        };
    }

    class Program
    {
        static void Main() => new App().Run();
    }
}
