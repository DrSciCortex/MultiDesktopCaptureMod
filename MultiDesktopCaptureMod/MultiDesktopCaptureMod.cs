using System.Reflection;
using System.Runtime.CompilerServices;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using ResoniteModLoader;

namespace MultiDesktopCaptureMod;

/// <summary>
/// Fixes several problems with the userspace Desktop dash screen on multi-monitor setups.
///
/// <para>
/// Empty Desktop screen: a dash saved by a client where <c>Engine.Config.DisableDesktop</c> was
/// true (Linux, Wine, any non-Windows platform) stores a <see cref="DesktopController"/> whose UI
/// was never built, because <c>DesktopController.OnAttach</c> skips construction entirely in that
/// case. The dash lives in the cloud, so a Windows client then loads that empty screen and shows a
/// blank Desktop tab with no controls. It never self-heals: the rebuild in
/// <c>DesktopScreen.OnLoading</c> only runs when the stored type version is below
/// <c>DesktopScreen.Version</c>, and the broken screen is already saved at the current version.
/// </para>
///
/// <para>
/// Share screen picker: the stock Share Screen button always shares the display shown in the
/// Desktop tab. With Follow Cursor on, that display snaps to wherever the mouse is, and the mouse
/// has to be over the Resonite window to press the button, so in practice only the display
/// Resonite is on can be shared. This adds a button that cycles which display is shared,
/// independently of the one being viewed.
/// </para>
/// </summary>
public class MultiDesktopCaptureMod : ResoniteMod {
	internal const string VERSION_CONSTANT = "1.4.2"; //Changing the version here updates it in all locations needed
	public override string Name => "MultiDesktopCaptureMod";
	public override string Author => "DrSciCortex";
	public override string Version => VERSION_CONSTANT;
	public override string Link => "https://github.com/DrSciCortex/MultiDesktopCaptureMod/";

	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> ENABLED =
		new("Enabled", "Master switch for all of this mod's behaviour.", () => true);

	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> REPAIR_EMPTY_SCREEN =
		new("RepairEmptyDesktopScreen", "Rebuild the Desktop dash screen if it was saved with no contents.", () => true);

	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> CLAMP_DISPLAY_INDEX =
		new("ClampDisplayIndex", "Fall back to the first display when the selected capture display no longer exists.", () => true);

	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> SHARE_SCREEN_PICKER =
		new("ShareScreenPicker", "Add a button to Desktop Controls that picks which display Share Screen shares. Takes effect the next time Desktop Controls is opened.", () => true);

	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> SHARE_WITHOUT_UI_LAYERS =
		new("Desktop share without UI layers", "Hide the video player's frame and controls on your screen shares, leaving only the picture. Applies to shares started after it is turned on.", () => false);

	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<int> DEFAULT_SHARE_FPS =
		new("Default share FPS", "Frame rate preselected in the screen share dialog: 15, 30 or 60.", () => 30);

	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<int> DEFAULT_SHARE_RESOLUTION =
		new("Default share resolution", "Vertical resolution preselected in the screen share dialog: 480, 720, 1080 or 2160. Ignored when it is taller than the shared display.", () => 1080);

	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> FIX_RESOLUTION_LABELS =
		new("FixResolutionLabels", "Fix the screen share dialog's resolution buttons, which Resonite labels in reverse order (the 1080 button reads 720p).", () => true);

	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> LOCAL_ONLY_OPTION =
		new("LocalOnlyOption", "Add a Local only checkbox to the screen share dialog. A local only share is shown just to you and is never encoded or sent to anyone.", () => true);

	// Height in meters of a local only desktop view, before scaling to the user.
	private const float LOCAL_VIEW_HEIGHT = 0.6f;

	// DesktopStreamConfigDialog._display is private.
	private static readonly FieldInfo? ConfigDisplayField = AccessTools.Field(typeof(DesktopStreamConfigDialog), "_display");

	// Local only checkboxes of open share dialogs.
	private static readonly ConditionalWeakTable<DesktopStreamConfigDialog, Checkbox> LocalOnlyCheckboxes = new();

	// Last Local only choice, so the checkbox keeps its state between shares this session.
	private static bool _localOnlyLastChoice;

	// The options DesktopStreamConfigDialog offers; anything else would select no button.
	private static readonly int[] ShareFpsOptions = [15, 30, 60];
	private static readonly int[] ShareResolutionOptions = [480, 720, 1080, 2160];

	// Overlays drawn above 'Video texture' inside the stock video player's 'Background mask'.
	private static readonly string[] PlayerUILayers = ["Drop shadow layout", "Volume area", "Title area", "Audio indicator", "Bottom area"];

	private const string SHARE_SCREEN_KEY = "MultiDesktopCaptureMod.ShareScreen";
	private const string STOP_SHARING_SCREEN_KEY = "MultiDesktopCaptureMod.StopSharingScreen";
	private const string LOCAL_ONLY_KEY = "MultiDesktopCaptureMod.LocalOnly";

	private static ModConfiguration? _config;

	// DesktopScreen.Setup() is private; it is what OnAttach and the version migration both call
	// to build the screen's contents.
	private static readonly MethodInfo? SetupMethod = AccessTools.Method(typeof(DesktopScreen), "Setup");

	// DesktopController._desktopTexture is protected; reading it directly avoids a per-frame
	// component search in the OnCommonUpdate prefix.
	private static readonly FieldInfo? DesktopTextureField = AccessTools.Field(typeof(DesktopController), "_desktopTexture");

	// DesktopControlDialog._streamButton is protected.
	private static readonly FieldInfo? StreamButtonField = AccessTools.Field(typeof(DesktopControlDialog), "_streamButton");

	// Display chosen for sharing, or -1 until one has been picked. Static because there is a
	// single local user and Desktop Controls is rebuilt every time it is opened, so the choice
	// has to outlive any one dialog.
	private static int _shareDisplayIndex = -1;

	// Dialogs that were built with the picker. Dialogs opened before the picker was enabled have
	// no cycle button, and are left entirely to the stock code.
	private static readonly ConditionalWeakTable<DesktopControlDialog, Button> CycleButtons = new();

	private static bool IsOn(ModConfigurationKey<bool> key) =>
		_config is not null
		&& _config.GetValue(ENABLED)
		&& _config.GetValue(key);

	public override void OnEngineInit() {
		_config = GetConfiguration();

		if (SetupMethod is null)
			Error("Could not find DesktopScreen.Setup(); empty-screen repair is disabled.");
		if (DesktopTextureField is null)
			Error("Could not find DesktopController._desktopTexture; display clamping is disabled.");
		if (StreamButtonField is null)
			Error("Could not find DesktopControlDialog._streamButton; the share screen picker is disabled.");

		Harmony harmony = new("dev.drscicortex.MultiDesktopCaptureMod");
		harmony.PatchAll();
	}

	private static DesktopTextureProvider? GetProvider(DesktopController controller) =>
		DesktopTextureField?.GetValue(controller) is SyncRef<DesktopTextureProvider> reference
			? reference.Target
			: null;

	private static Button? GetStreamButton(DesktopControlDialog dialog) =>
		StreamButtonField?.GetValue(dialog) is SyncRef<Button> reference
			? reference.Target
			: null;

	/// <summary>
	/// Returns the display index to share, defaulting to the display shown in the Desktop tab and
	/// pulling a stale choice back into range if a display has gone away.
	/// </summary>
	private static int GetShareDisplayIndex(DesktopControlDialog dialog) {
		int count = dialog.InputInterface?.DisplayCount ?? 0;
		if (count <= 0) return 0;
		if (_shareDisplayIndex < 0 || _shareDisplayIndex >= count)
			_shareDisplayIndex = MathX.Clamp(dialog.Index.Value, 0, count - 1);
		return _shareDisplayIndex;
	}

	private static Display? GetShareDisplay(DesktopControlDialog dialog) =>
		dialog.InputInterface?.TryGetDisplay(GetShareDisplayIndex(dialog));

	private static PriviledgedResourceToken? GetShareToken(DesktopControlDialog dialog, Display? display) =>
		display is null
			? null
			: DesktopStreamTokenManager.TryGetActiveToken(dialog.Engine.WorldManager.FocusedWorld, display);

	/// <summary>
	/// Rebuilds a Desktop screen whose contents are missing. Runs a frame after the screen starts
	/// so that loading has finished resolving the screen's own references.
	/// </summary>
	[HarmonyPatch(typeof(DesktopScreen), "OnStart")]
	private static class DesktopScreen_OnStart_Patch {
		private static void Postfix(DesktopScreen __instance) {
			if (SetupMethod is null || !IsOn(REPAIR_EMPTY_SCREEN)) return;

			// On a client that has the desktop disabled, Setup() would build nothing anyway.
			if (Engine.Config?.DisableDesktop == true) return;
			if (__instance.World != Userspace.UserspaceWorld) return;

			__instance.RunInUpdates(1, () => Repair(__instance));
		}

		private static void Repair(DesktopScreen screen) {
			if (screen.IsDestroyed) return;

			Canvas canvas = screen.ScreenCanvas;
			if (canvas is null) return;

			// A screen built normally always carries a DesktopTextureProvider. Testing for the
			// component rather than for DesktopController's reference to it keeps this robust
			// regardless of when sync references finish resolving.
			if (canvas.Slot.GetComponentInChildren<DesktopTextureProvider>() is not null) return;

			Msg("Desktop screen has no contents (saved by a client with the desktop disabled); rebuilding it.");
			canvas.Slot.DestroyChildren();
			SetupMethod!.Invoke(screen, null);

			// Mark the dash dirty so the repair is saved and this only has to happen once.
			screen.NotifyModified();
		}
	}

	/// <summary>
	/// Keeps the capture display index in range. DesktopController silently does nothing when
	/// TryGetDisplay returns null, and the index is persisted in the dash, so unplugging the
	/// selected monitor otherwise leaves a permanently blank desktop view.
	/// </summary>
	[HarmonyPatch(typeof(DesktopController), "OnCommonUpdate")]
	private static class DesktopController_OnCommonUpdate_Patch {
		private static void Prefix(DesktopController __instance) {
			if (DesktopTextureField is null || !IsOn(CLAMP_DISPLAY_INDEX)) return;

			InputInterface input = __instance.InputInterface;
			if (input is null || input.DisplayCount <= 0) return;

			DesktopTextureProvider? provider = GetProvider(__instance);
			if (provider is null) return;

			int index = provider.DisplayIndex.Value;
			if (index >= 0 && index < input.DisplayCount) return;

			Msg($"Capture display {index} no longer exists ({input.DisplayCount} connected); falling back to display 0.");
			provider.DisplayIndex.Value = 0;
		}
	}

	/// <summary>
	/// Adds a ▶ button directly after Share Screen that cycles which display will be shared.
	/// </summary>
	[HarmonyPatch(typeof(DesktopControlDialog), "OnAttach")]
	private static class DesktopControlDialog_OnAttach_Patch {
		private static void Postfix(DesktopControlDialog __instance) {
			if (!IsOn(SHARE_SCREEN_PICKER) || !__instance.CanOperate) return;

			Button? stream = GetStreamButton(__instance);
			if (stream is null) return;

			UIBuilder ui = new(stream.Slot.Parent);
			RadiantUI_Constants.SetupDefaultStyle(ui);
			ui.Style.FlexibleWidth = 0.5f;
			Button cycle = ui.Button((LocaleString)"▶");
			cycle.Slot.Name = "Share Screen Cycle";
			cycle.Slot.InsertAtIndex(stream.Slot.ChildIndex + 1);

			// LocalPressed rather than the synced Pressed delegate: the dialog sits inside the
			// saved dash, and a lambda cannot be serialized into it.
			cycle.LocalPressed += (_, _) => {
				int count = __instance.InputInterface?.DisplayCount ?? 0;
				if (count > 1)
					_shareDisplayIndex = (GetShareDisplayIndex(__instance) + 1) % count;
			};

			CycleButtons.AddOrUpdate(__instance, cycle);
		}
	}

	/// <summary>
	/// Replaces the stock label update, which reports the sharing state of the viewed display,
	/// with one for the picked display. This also avoids the stock code's unguarded
	/// InputInterface.GetDisplay(Index), which throws once the viewed display is out of range.
	/// </summary>
	[HarmonyPatch(typeof(DesktopControlDialog), "OnCommonUpdate")]
	private static class DesktopControlDialog_OnCommonUpdate_Patch {
		private static bool Prefix(DesktopControlDialog __instance) {
			if (!CycleButtons.TryGetValue(__instance, out Button? cycle)) return true;

			Button? stream = GetStreamButton(__instance);
			if (stream is null) return true;

			int count = __instance.InputInterface?.DisplayCount ?? 0;
			int index = GetShareDisplayIndex(__instance);
			bool sharing = GetShareToken(__instance, GetShareDisplay(__instance)) is not null;

			stream.LabelTextField.SetLocalized(sharing ? STOP_SHARING_SCREEN_KEY : SHARE_SCREEN_KEY, "{0}", "n", (index + 1).ToString());
			cycle.Slot.ActiveSelf = count > 1;
			return false;
		}
	}

	/// <summary>
	/// Starts or stops sharing the picked display instead of the viewed one.
	/// </summary>
	[HarmonyPatch(typeof(DesktopControlDialog), "OnStream")]
	private static class DesktopControlDialog_OnStream_Patch {
		private static bool Prefix(DesktopControlDialog __instance) {
			if (!CycleButtons.TryGetValue(__instance, out _)) return true;
			if (!__instance.CanOperate) return false;

			Display? display = GetShareDisplay(__instance);
			if (display is null) return false;

			PriviledgedResourceToken? token = GetShareToken(__instance, display);
			if (token is not null)
				token.Dispose();
			else
				__instance.Slot.OpenModalOverlay(new float2(0.4f, 0.6f)).Slot.AttachComponent<DesktopStreamConfigDialog>().Initialize(display);
			return false;
		}
	}

	/// <summary>
	/// Replaces the screen share dialog's hardcoded defaults (60 fps, and the tallest of
	/// 1080/720/480 the display allows) with the configured ones.
	/// </summary>
	[HarmonyPatch(typeof(DesktopStreamConfigDialog), nameof(DesktopStreamConfigDialog.Initialize))]
	private static class DesktopStreamConfigDialog_Initialize_Patch {
		private static void Postfix(DesktopStreamConfigDialog __instance, Display display) {
			if (_config is null || !_config.GetValue(ENABLED)) return;

			int fps = _config.GetValue(DEFAULT_SHARE_FPS);
			if (ShareFpsOptions.Contains(fps))
				__instance.FPS.Value = fps;

			// The dialog disables resolutions taller than the display, so leave its own choice
			// in place rather than preselect a disabled option.
			int resolution = _config.GetValue(DEFAULT_SHARE_RESOLUTION);
			if (ShareResolutionOptions.Contains(resolution) && resolution <= display.Resolution.y)
				__instance.VerticalResolution.Value = resolution;

			if (_config.GetValue(FIX_RESOLUTION_LABELS))
				FixResolutionLabels(__instance);

			if (_config.GetValue(LOCAL_ONLY_OPTION))
				AddLocalOnlyCheckbox(__instance);
		}

		/// <summary>
		/// Relabels the resolution buttons from the values they actually set. The stock dialog
		/// pairs the options { 2160, 1080, 720, 480 } with the labels { "480p", "720p", "1080p",
		/// "4K" }, so each button shows the wrong resolution: 1080 reads "720p", and pressing
		/// "1080p" selects 720.
		/// </summary>
		private static void FixResolutionLabels(DesktopStreamConfigDialog dialog) {
			foreach (ButtonValueSet<int> option in dialog.Slot.GetComponentsInChildren<ButtonValueSet<int>>(o => o.TargetValue.Target == dialog.VerticalResolution)) {
				Button? button = option.Slot.GetComponent<Button>();
				if (button is null) continue;

				int value = option.SetValue.Value;
				button.LabelTextField.Value = value >= 2160 ? "4K" : $"{value}p";
			}
		}

		/// <summary>
		/// Adds a Local only checkbox directly below Include desktop audio.
		/// </summary>
		private static void AddLocalOnlyCheckbox(DesktopStreamConfigDialog dialog) {
			Checkbox? audio = dialog.Slot.GetComponentInChildren<Checkbox>(c => c.TargetState.Target == dialog.DesktopAudio);
			if (audio is null) return;

			// The audio checkbox's row is the direct child of the dialog's vertical layout.
			Slot row = audio.Slot;
			while (row.Parent is not null && row.Parent.GetComponent<VerticalLayout>() is null)
				row = row.Parent;
			Slot? layout = row.Parent;
			if (layout is null) return;

			UIBuilder ui = new(layout);
			RadiantUI_Constants.SetupDefaultStyle(ui);
			ui.Style.MinHeight = 48f;
			Checkbox localOnly = ui.Checkbox(LOCAL_ONLY_KEY.AsLocaleKey(), _localOnlyLastChoice);

			Slot newRow = localOnly.Slot;
			while (newRow.Parent != layout)
				newRow = newRow.Parent;
			newRow.InsertAtIndex(row.ChildIndex + 1);

			LocalOnlyCheckboxes.AddOrUpdate(dialog, localOnly);
		}
	}

	/// <summary>
	/// Starts a local only share when Local only is ticked: a desktop view that exists only on
	/// this machine, built from local slots and components, with nothing encoded or streamed.
	/// It is tied to the display's share token like a normal share, so the Stop Sharing Screen
	/// button in Desktop Controls closes it.
	/// </summary>
	[HarmonyPatch(typeof(DesktopStreamConfigDialog), "OnStartStreaming")]
	private static class DesktopStreamConfigDialog_OnStartStreaming_Patch {
		private static bool Prefix(DesktopStreamConfigDialog __instance) {
			if (!LocalOnlyCheckboxes.TryGetValue(__instance, out Checkbox? localOnly)) return true;

			_localOnlyLastChoice = localOnly.State.Value;
			if (!_localOnlyLastChoice) return true;

			if (ConfigDisplayField?.GetValue(__instance) is not Display display) return true;

			World world = __instance.Engine.WorldManager.FocusedWorld;
			PriviledgedResourceToken token = DesktopStreamTokenManager.GetTokenOrCreate(__instance.World, world, display);
			world.RunSynchronously(() => SpawnLocalView(world, token, display));
			__instance.Slot.CloseModalOverlay();
			return false;
		}

		private static void SpawnLocalView(World world, PriviledgedResourceToken token, Display display) {
			if (!token.IsGranted) return;

			int displayIndex = display.DisplayIndex;
			float aspect = display.Resolution.y > 0 ? (float)display.Resolution.x / display.Resolution.y : 16f / 9f;
			float2 size = new(LOCAL_VIEW_HEIGHT * aspect, LOCAL_VIEW_HEIGHT);

			// A synced handle carries the transform, collider and Grabbable, so grabbing, moving
			// and scaling work the normal way; a slot that is local end to end cannot be grabbed.
			// The handle holds nothing private, is never saved, and goes away if you leave.
			Slot handle = world.LocalUserSpace.AddSlot($"Local Desktop View {displayIndex + 1}", persistent: false);
			handle.DestroyWhenUserLeaves(world.LocalUser);

			// Off for everyone but you, so other users cannot hit or grab an invisible box.
			BoxCollider collider = handle.AttachComponent<BoxCollider>();
			collider.Size.Value = new float3(size.x, size.y, 0f);
			collider.EnabledField.Value = false;
			collider.EnabledField.OverrideForUser(world.LocalUser, true);

			Grabbable grabbable = handle.AttachComponent<Grabbable>();
			grabbable.Scalable.Value = true;
			grabbable.OnlyUsers.Add().Target = world.LocalUser;

			// Everything that shows the desktop lives in a local child, which exists only on this
			// machine. Same arrangement SharedDesktopTexture uses for its own capture: a provider
			// on a local slot, allowed to capture by a token derived from the display's share token.
			Slot content = handle.AddLocalSlot("Content");

			DesktopTextureProvider provider = content.AttachComponent<DesktopTextureProvider>();
			provider.DisplayIndex.Value = displayIndex;
			provider.AssignToken(new PriviledgedResourceToken(token, () => provider.DisplayIndex.Value == displayIndex));

			UnlitMaterial material = content.AttachComponent<UnlitMaterial>();
			material.Texture.Target = provider;
			material.Sidedness.Value = Sidedness.Double;
			content.AttachQuad(size, material, collider: false);

			handle.PositionInFrontOfUser(float3.Backward);

			token.OnRevoked += () => world.RunSynchronously(() => {
				if (!handle.IsDestroyed) handle.Destroy();
			});

			Msg($"Started local only desktop view of display {displayIndex + 1}.");
		}
	}

	/// <summary>
	/// Hides the video player's UI layers on screen shares. Starting a share spawns the user's
	/// VideoStream favorite and binds it to the desktop through SetSource, so a SharedDesktopTexture
	/// source identifies a screen share rather than any other video stream. The layers are hidden
	/// in the world itself, so everyone in the session sees only the picture.
	/// </summary>
	[HarmonyPatch(typeof(VideoStreamInterface), nameof(VideoStreamInterface.SetSource))]
	private static class VideoStreamInterface_SetSource_Patch {
		private static void Postfix(VideoStreamInterface __instance, IAssetProvider<ITexture2D> texture) {
			if (texture is not SharedDesktopTexture || !IsOn(SHARE_WITHOUT_UI_LAYERS)) return;

			// Deferred a frame so the freshly spawned player has finished initializing.
			__instance.RunInUpdates(1, () => HideUILayers(__instance));
		}

		private static void HideUILayers(VideoStreamInterface player) {
			if (player.IsDestroyed) return;

			Slot mask = player.Slot.FindChildInHierarchy("Background mask");
			int hidden = 0;
			if (mask is not null)
				foreach (Slot layer in mask.Children)
					if (PlayerUILayers.Contains(layer.Name)) {
						layer.ActiveSelf = false;
						hidden++;
					}

			// Layer names come from the stock video player; a custom VideoStream favorite may differ.
			if (hidden == 0)
				Warn("Desktop share without UI layers: no known UI layers found on the video player, so nothing was hidden. Is your VideoStream favorite a custom player?");
		}
	}
}
