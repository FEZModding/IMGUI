using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using SDL2;
using System.Runtime.InteropServices;
using static SDL2.SDL;

namespace ImGuiNET;

/// <summary>
/// Hacky hack to get the horizontal mouse wheel data straight from SDL.<br />
/// just get the value from <see cref="wheel_x"/>
/// and make sure to set <see cref="hadGameTick"/> to true before each draw
/// </summary>
internal class HorizontalScrollHack
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SDL_PollEventDelegate(out SDL.SDL_Event sdlEvent);
    private delegate int HookDelegate(SDL_PollEventDelegate orig, out SDL.SDL_Event sdlEvent);
    private static NativeHook _nativeDetour;
    private static HookDelegate _hookDelegate;
    static HorizontalScrollHack()
    {
        try
        {
            string libName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "SDL2.dll" :
                             RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libSDL2.dylib" : "libSDL2-2.0.so.0";

            if (!DynDll.TryOpenLibrary(libName, out IntPtr libPtr) ||
                !libPtr.TryGetExport("SDL_PollEvent", out IntPtr funcPtr))
            {
                throw new EntryPointNotFoundException("Could not resolve native SDL_PollEvent export.");
            }

            // 3. Keep a strong reference to the managed delegate so the GC does not collect it
            _hookDelegate = Hooked_SDL_PollEvent;
            IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);

            _nativeDetour = new NativeHook(funcPtr, _hookDelegate);
        }
        catch (Exception)
        {
        }
    }
    public static float wheel_x = 0;
    public static bool hadGameTick = false;
    private const int maxEvents = 16;
    private static readonly SDL_Event[] events = new SDL_Event[maxEvents];
    private static int Hooked_SDL_PollEvent(SDL_PollEventDelegate _origPollEvent, out SDL.SDL_Event sdlEvent)
    {
        if (_nativeDetour.IsApplied)
        {
            if (hadGameTick)
            {
                wheel_x = 0;
            }
            int count = SDL_PeepEvents(events, maxEvents, SDL_eventaction.SDL_PEEKEVENT, SDL_EventType.SDL_MOUSEWHEEL, SDL_EventType.SDL_MOUSEWHEEL);
            for (int i = 0; i < count; i++)
            {
                var etv = events[i];
                if (etv.type == SDL_EventType.SDL_MOUSEWHEEL)
                {
                    var wheel = events[i].wheel;
                    wheel_x = -wheel.x;
                    hadGameTick = false;
                }
            }
        }
        return _origPollEvent(out sdlEvent);
    }
}
