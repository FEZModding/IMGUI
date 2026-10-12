using Microsoft.Xna.Framework.Input;
using FezEngine.Components;
using Microsoft.Xna.Framework;
using MonoMod.RuntimeDetour;

namespace ImGuiNET;

internal partial class ImGuiRenderer
{
    private Hook _inputUpdateHook;
    private bool _captureInput;
    private Hook _startTextInputHook;
    private Hook _stopTextInputHook;
    private bool _wantTextInput;
    private bool _textInputActive;
    private bool _externalTextInputRequested;
    private bool _updatingTextInput;

    private delegate void orig_Update(InputManager self, GameTime gameTime);

    private void InitializeTextInput()
    {
        // Newer FNA exposes a state query; older game DLLs do not.
        // After initialization, the hooks below track the state without requiring that API.
        _textInputActive = typeof(TextInputEXT).GetMethod("IsTextInputActive", Type.EmptyTypes)
                              ?.Invoke(null, null) is true;
        _externalTextInputRequested = _textInputActive;

        var startMethod = typeof(TextInputEXT).GetMethod(nameof(TextInputEXT.StartTextInput))
                          ?? throw new InvalidOperationException("Could not find StartTextInput method on TextInputEXT");
        var stopMethod = typeof(TextInputEXT).GetMethod(nameof(TextInputEXT.StopTextInput))
                         ?? throw new InvalidOperationException("Could not find StopTextInput method on TextInputEXT");

        // FNA's text input switch is shared by every UI.
        // Track other callers separately so either UI can close without stopping character events for the other.
        _startTextInputHook = new Hook(startMethod, (Action orig) =>
        {
            if (!_updatingTextInput)
            {
                _externalTextInputRequested = true;
            }

            orig();
            _textInputActive = true;
        });
        _stopTextInputHook = new Hook(stopMethod, (Action orig) =>
        {
            if (!_updatingTextInput)
            {
                _externalTextInputRequested = false;
            }

            if (!_wantTextInput)
            {
                orig();
                _textInputActive = false;
            }
        });
    }

    private void UpdateTextInput(bool wantTextInput)
    {
        _wantTextInput = wantTextInput;
        _updatingTextInput = true;
        try
        {
            if (_wantTextInput && !_textInputActive)
            {
                TextInputEXT.StartTextInput();
            }
            else if (!_wantTextInput && !_externalTextInputRequested && _textInputActive)
            {
                TextInputEXT.StopTextInput();
            }
        }
        finally
        {
            _updatingTextInput = false;
        }
    }

    private void DisposeTextInput()
    {
        UpdateTextInput(false);
        _stopTextInputHook?.Dispose();
        _stopTextInputHook = null;
        _startTextInputHook?.Dispose();
        _startTextInputHook = null;
    }

    private void InitializeInputCapture()
    {
        if (_inputManager == null)
        {
            return;
        }

        var updateMethod = typeof(InputManager).GetMethod(nameof(InputManager.Update), [typeof(GameTime)])
                           ?? throw new InvalidOperationException("Could not find Update method on InputManager");

        _inputUpdateHook = new Hook(updateMethod, (orig_Update orig, InputManager self, GameTime gameTime) =>
        {
            orig(self, gameTime);

            // Keep raw input transitions current, but suppress game actions while ImGui captures input.
            // Leave Enabled to the game and other mods, including FEZUG's console.
            if (self == _inputManager && _captureInput)
            {
                self.Reset();
            }
        });
    }

    /// <summary>
    /// Maps XNA Keys to ImGui key codes for input handling.
    /// </summary>
    private static readonly Dictionary<Keys, ImGuiKey> KeyMappings = new()
    {
        [Keys.Back] = ImGuiKey.Backspace,
        [Keys.Tab] = ImGuiKey.Tab,
        [Keys.Enter] = ImGuiKey.Enter,
        [Keys.CapsLock] = ImGuiKey.CapsLock,
        [Keys.Escape] = ImGuiKey.Escape,
        [Keys.Space] = ImGuiKey.Space,
        [Keys.PageUp] = ImGuiKey.PageUp,
        [Keys.PageDown] = ImGuiKey.PageDown,
        [Keys.End] = ImGuiKey.End,
        [Keys.Home] = ImGuiKey.Home,
        [Keys.Left] = ImGuiKey.LeftArrow,
        [Keys.Right] = ImGuiKey.RightArrow,
        [Keys.Up] = ImGuiKey.UpArrow,
        [Keys.Down] = ImGuiKey.DownArrow,
        [Keys.PrintScreen] = ImGuiKey.PrintScreen,
        [Keys.Insert] = ImGuiKey.Insert,
        [Keys.Delete] = ImGuiKey.Delete,
        [Keys.Multiply] = ImGuiKey.KeypadMultiply,
        [Keys.Add] = ImGuiKey.KeypadAdd,
        [Keys.Subtract] = ImGuiKey.KeypadSubtract,
        [Keys.Decimal] = ImGuiKey.KeypadDecimal,
        [Keys.Divide] = ImGuiKey.KeypadDivide,
        [Keys.NumLock] = ImGuiKey.NumLock,
        [Keys.Scroll] = ImGuiKey.ScrollLock,
        [Keys.LeftShift] = ImGuiKey.ModShift,
        [Keys.LeftControl] = ImGuiKey.ModCtrl,
        [Keys.LeftAlt] = ImGuiKey.ModAlt,
        [Keys.OemSemicolon] = ImGuiKey.Semicolon,
        [Keys.OemPlus] = ImGuiKey.Equal,
        [Keys.OemComma] = ImGuiKey.Comma,
        [Keys.OemMinus] = ImGuiKey.Minus,
        [Keys.OemPeriod] = ImGuiKey.Period,
        [Keys.OemQuestion] = ImGuiKey.Slash,
        [Keys.OemTilde] = ImGuiKey.GraveAccent,
        [Keys.OemOpenBrackets] = ImGuiKey.LeftBracket,
        [Keys.OemCloseBrackets] = ImGuiKey.RightBracket,
        [Keys.OemPipe] = ImGuiKey.Backslash,
        [Keys.OemQuotes] = ImGuiKey.Apostrophe,
        [Keys.BrowserBack] = ImGuiKey.AppBack,
        [Keys.BrowserForward] = ImGuiKey.AppForward
    };
    
    /// <summary>
    /// Dynamically populates key mappings for alphanumeric keys, numpad, and function keys.
    /// </summary>
    private static void PopulateKeyMappings()
    {
        foreach (Keys key in Enum.GetValues(typeof(Keys)))
        {
            switch (key)
            {
                case >= Keys.D0 and <= Keys.D9:
                    KeyMappings.Add(key, ImGuiKey._0 + (key - Keys.D0));
                    break;
                    
                case >= Keys.A and <= Keys.Z:
                    KeyMappings.Add(key, ImGuiKey.A + (key - Keys.A));
                    break;
                    
                case >= Keys.NumPad0 and <= Keys.NumPad9:
                    KeyMappings.Add(key, ImGuiKey.Keypad0 + (key - Keys.NumPad0));
                    break;
                    
                case >= Keys.F1 and <= Keys.F24:
                    KeyMappings.Add(key, ImGuiKey.F1 + (key - Keys.F1));
                    break;
            }
        }
    }
}
