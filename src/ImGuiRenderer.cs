using System.Diagnostics;
using System.Runtime.InteropServices;
using FezEngine.Components;
using FezEngine.Effects;
using FezEngine.Structure;
using FezEngine.Structure.Geometry;
using FezEngine.Tools;
using IMGUI.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ImGuiNET;

/// <summary>
/// Renders Dear ImGui user interfaces in FEZ by integrating ImGui.NET with the game's graphics pipeline.
/// </summary>
internal partial class ImGuiRenderer : IDisposable
{
    private readonly Game _game;

    private readonly GraphicsDevice _graphicsDevice;

    private readonly InputManager _inputManager;

    private Texture2D _fontTexture;

    private Mesh _mesh;

    private BaseEffect _baseEffect;

    private float _previousScrollWheelValue;

    private const float FallbackFrameTime = 1f / 60f;

    private const float WheelDelta = 120f;

    public static ImGuiRenderer Create(Game game)
    {
        var renderer = new ImGuiRenderer(game);
        renderer.Initialize();
        renderer.RebuildFontAtlas();
        return renderer;
    }

    private ImGuiRenderer(Game game)
    {
        _game = game;
        _graphicsDevice = game.GraphicsDevice;
        _inputManager = ServiceHelper.Get<IInputManager>() as InputManager;
    }

    /// <summary>
    /// Initializes the ImGui context, rendering resources, input mappings, and event handlers.
    /// </summary>
    private void Initialize()
    {
        _baseEffect = new DefaultEffect.TexturedVertexColored();
        _mesh = new Mesh
        {
            Effect = _baseEffect,
            Blending = BlendingMode.Alphablending,
            Culling = CullMode.None,
            AlwaysOnTop = true,
            DepthWrites = false,
            CustomRenderingHandler = (_, _) => RenderMesh()
        };

        var context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        SetupStyle();
        PopulateKeyMappings();
            
        TextInputEXT.TextInput += HandleInput;
        InitializeTextInput();
        InitializeInputCapture();
    }

    /// <summary>
    /// Rebuilds the font atlas by extracting texture data from ImGui and creating a GPU texture.
    /// </summary>
    private unsafe void RebuildFontAtlas()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(
            out byte* pixelData,
            out var width,
            out var height,
            out var bytesPerPixel);

        var pixels = new byte[width * height * bytesPerPixel];
        Marshal.Copy(new IntPtr(pixelData), pixels, 0, pixels.Length);

        _fontTexture = new Texture2D(_graphicsDevice, width, height, false, SurfaceFormat.Color);
        _fontTexture.SetData(pixels);
        io.Fonts.SetTexID(BindTexture(_fontTexture));
        io.Fonts.ClearTexData();
    }

    /// <summary>
    /// Updates ImGui input state and begins a new ImGui frame.
    /// </summary>
    /// <remarks>
    /// Call this before rendering ImGui UI.
    /// </remarks>
    /// <param name="gameTime">Current game timing information.</param>
    public void BeforeLayout(GameTime gameTime)
    {
        var io = ImGui.GetIO();
        var delta = (float)gameTime.ElapsedGameTime.TotalSeconds;
        io.DeltaTime = delta > 0f ? delta : FallbackFrameTime;

        #region Update inputs
        {
            var mouse = Mouse.GetState();
            io.AddMousePosEvent(mouse.X, mouse.Y);
            io.AddMouseButtonEvent(0, mouse.LeftButton == ButtonState.Pressed);
            io.AddMouseButtonEvent(1, mouse.RightButton == ButtonState.Pressed);
            io.AddMouseButtonEvent(2, mouse.MiddleButton == ButtonState.Pressed);
            io.AddMouseButtonEvent(3, mouse.XButton1 == ButtonState.Pressed);
            io.AddMouseButtonEvent(4, mouse.XButton2 == ButtonState.Pressed);
            io.AddMouseWheelEvent(0, (mouse.ScrollWheelValue - _previousScrollWheelValue) / WheelDelta);
            _previousScrollWheelValue = mouse.ScrollWheelValue;

            var keyboard = Keyboard.GetState();
            foreach (Keys key in KeyMappings.Keys)
            {
                io.AddKeyEvent(KeyMappings[key], keyboard.IsKeyDown(key));
            }
        }
        #endregion

        io.DisplaySize = new System.Numerics.Vector2
        {
            X = _graphicsDevice.PresentationParameters.BackBufferWidth,
            Y = _graphicsDevice.PresentationParameters.BackBufferHeight
        };
        io.DisplayFramebufferScale = new System.Numerics.Vector2(1, 1);

        ImGui.NewFrame();
        UpdateTextInput(_game.IsActive && io.WantTextInput);
        _captureInput = io.WantCaptureMouse || io.WantCaptureKeyboard;
        _game.IsMouseVisible = io.WantCaptureMouse;
    }

    /// <summary>
    /// Renders the ImGui draw data to the screen. Call this after all ImGui UI code.
    /// </summary>
    public unsafe void AfterLayout()
    {
        ImGui.Render();
        CleanupDeadTextures();

        var io = ImGui.GetIO();
        var drawData = ImGui.GetDrawData();
        drawData.ScaleClipRects(io.DisplayFramebufferScale);

        _baseEffect.ForcedProjectionMatrix = Matrix.CreateOrthographicOffCenter(0f, io.DisplaySize.X, io.DisplaySize.Y, 0f, -1f, 1f);
        _baseEffect.ForcedViewMatrix = Matrix.Identity;

        _mesh.ClearGroups();
        for (var n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];

            var vertexCount = cmdList.VtxBuffer.Size;
            var vertexPtr = (ImDrawVert*)cmdList.VtxBuffer.Data;
            Debug.Assert(vertexPtr != null);

            var indexPtr = (ushort*)cmdList.IdxBuffer.Data;
            Debug.Assert(indexPtr != null);

            var cmdCount = cmdList.CmdBuffer.Size;
            var cmdPtr = (ImDrawCmd*)cmdList.CmdBuffer.Data;
            Debug.Assert(cmdPtr != null);

            var vertices = new VertexPositionColorTextureInstance[vertexCount];
            for (var i = 0; i < vertexCount; i++)
            {
                var vert = vertexPtr[i];
                var position = new Vector3 { X = vert.pos.X, Y = vert.pos.Y };
                var color = new Color { PackedValue = vert.col };
                var texCoord = new Vector2 { X = vert.uv.X, Y = vert.uv.Y };
                vertices[i] = new VertexPositionColorTextureInstance(position, color, texCoord);
            }

            for (var i = 0; i < cmdCount; i++)
            {
                var cmd = cmdPtr[i];
                if (cmd.ElemCount == 0)
                {
                    continue;
                }

                var indices = new int[cmd.ElemCount];
                for (var j = 0; j < cmd.ElemCount; j++)
                {
                    indices[j] = indexPtr[cmd.IdxOffset + j];
                }

                var group = _mesh.AddGroup();
                group.Geometry = new IndexedUserPrimitives<VertexPositionColorTextureInstance>(vertices, indices, PrimitiveType.TriangleList);
                group.Texture = GetBoundTexture(cmd.TextureId);
                group.CustomData = new Rectangle
                {
                    X = (int)cmd.ClipRect.X,
                    Y = (int)cmd.ClipRect.Y,
                    Width = (int)(cmd.ClipRect.Z - cmd.ClipRect.X),
                    Height = (int)(cmd.ClipRect.W - cmd.ClipRect.Y)
                };
            }
        }

        _mesh.Draw();   // Triggers the RenderMesh method
    }

    /// <summary>
    /// Renders mesh groups with scissor rectangle clipping applied to each group.
    /// </summary>
    private void RenderMesh()
    {
        var rasterCombiner = _graphicsDevice.GetRasterCombiner();
        var oldScissorTestEnable = rasterCombiner.GetScissorTestEnable();
        var oldScissorRect = _graphicsDevice.ScissorRectangle;

        rasterCombiner.SetScissorTestEnable(true);
        foreach (var group in _mesh.Groups)
        {
            if (group.Enabled && group.Geometry != null && group.CustomData is Rectangle scissor)
            {
                _graphicsDevice.ScissorRectangle = scissor;
                group.Draw(_baseEffect);
            }
        }

        rasterCombiner.SetScissorTestEnable(oldScissorTestEnable);
        _graphicsDevice.ScissorRectangle = oldScissorRect;
    }

    /// <summary>
    /// Handles text input events and forwards them to ImGui, excluding tab characters.
    /// </summary>
    /// <param name="c">The input character to process.</param>
    private static void HandleInput(char c)
    {
        if (c != '\t')
        {
            ImGui.GetIO().AddInputCharacter(c);
        }
    }

    /// <summary>
    /// Releases all graphics resources including textures, meshes, and effects.
    /// </summary>
    public void Dispose()
    {
        _inputUpdateHook?.Dispose();
        _inputUpdateHook = null;
        TextInputEXT.TextInput -= HandleInput;
        DisposeTextInput();

        if (_fontTexture != null)
        {
            UnbindTexture(_fontTexture);
            _fontTexture.Dispose();
        }

        if (_mesh != null && _baseEffect != null)
        {
            _mesh.Dispose();
            _baseEffect.Dispose();
        }
    }
}