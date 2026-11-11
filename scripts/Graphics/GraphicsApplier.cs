using Godot;

namespace SteamEarth;

/// <summary>
/// Applique les réglages graphiques qui concernent tout l'écran : anticrénelage, résolution 3D, synchronisation verticale,
/// limite d'images par seconde, occlusion ambiante, halo lumineux et ombres du soleil.
/// Les couches (terrain, arbres, bâtiments…) appliquent elles-mêmes leurs réglages en écoutant GraphicsQuality.Changed.
/// </summary>
public partial class GraphicsApplier : Node
{
    public Godot.Environment Env;
    public DirectionalLight3D Sun;

    public override void _Ready()
    {
        GraphicsQuality.Changed += OnChanged;
        Apply();
    }

    public override void _ExitTree() => GraphicsQuality.Changed -= OnChanged;

    void OnChanged(QualityProfile now, QualityProfile previous) => Apply();

    void Apply()
    {
        var q = GraphicsQuality.Current;
        string method = RenderingServer.GetCurrentRenderingMethod();
        bool forward = method == "forward_plus";
        bool compat = method == "gl_compatibility";

        var vp = GetViewport();
        // FXAA n'existe pas en mode compatibilité OpenGL : on le remplace par du MSAA 2×
        bool fxaa = q.ScreenSpaceAA != Viewport.ScreenSpaceAAEnum.Disabled;
        vp.Msaa3D = compat && fxaa && q.Msaa == Viewport.Msaa.Disabled ? Viewport.Msaa.Msaa2X : q.Msaa;
        if (!compat) vp.ScreenSpaceAA = q.ScreenSpaceAA;
        float scale = GraphicsQuality.RenderScale;
        vp.Scaling3DScale = scale;
        // FSR 1 (plus net que l'étirement simple) quand on réduit la résolution, si le moteur de rendu le permet
        vp.Scaling3DMode = scale < 0.999f && !compat ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
        if (!compat) vp.TextureMipmapBias = scale < 0.999f ? Mathf.Log(scale) / Mathf.Log(2f) : 0f;

        DisplayServer.WindowSetVsyncMode(GraphicsQuality.VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = GraphicsQuality.MaxFps;

        if (Env != null)
        {
            Env.SsaoEnabled = forward && q.Ssao;   // occlusion ambiante : Forward+ seulement
            if (Env.SsaoEnabled)
            {
                // ombre de contact au pied des murs, sous les avant-toits et entre les brins d'herbe
                Env.SsaoRadius = q.Level >= QualityLevel.Ultra ? 1.6f : 1.2f;
                Env.SsaoIntensity = 2.2f;
                Env.SsaoDetail = 0.6f;
            }
            Env.SsilEnabled = forward && q.Ssil;   // lumière renvoyée par les murs et le sol
            Env.GlowEnabled = !compat && q.Glow;
        }

        if (Sun != null)
        {
            Sun.ShadowEnabled = q.Shadows;
            Sun.DirectionalShadowMaxDistance = q.ShadowMaxDistance;
            Sun.DirectionalShadowMode = q.ShadowSplits;
            Sun.ShadowBias = 0.05f;
            Sun.ShadowNormalBias = 1.5f;
            Sun.DirectionalShadowBlendSplits = q.Level >= QualityLevel.High;
            // ombres douces : pénombre réelle du soleil (nette au pied d'un objet, floue au bout de son ombre)
            Sun.LightAngularDistance = compat ? 0f : q.SunAngularSize;
            RenderingServer.DirectionalShadowAtlasSetSize(q.ShadowAtlasSize, true);
            RenderingServer.DirectionalSoftShadowFilterSetQuality(q.ShadowFilter);
        }
    }
}
