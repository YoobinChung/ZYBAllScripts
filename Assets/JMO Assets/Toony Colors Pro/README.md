# Shader Generator 2 Minimal Import

Copy the `Assets` folder in this bundle into another Unity project.

This bundle contains the reduced Shader Generator 2 scripts plus the minimum Toony Colors Pro editor/runtime assets they depend on:

- `Editor/Shader Generator`
- `Editor/Utils`
- `Editor/Shader Importer`
- `Shader Templates 2`
- `Shaders/Hybrid 2`
- `Editor/Inspectors/MaterialInspector_Hybrid.cs`
- `Editor/TCP2_Menu.cs`

Notes:

- Hybrid Shader 2 references URP shader library includes for URP passes, so URP support in the target project is recommended.
- Keep the included `.meta` files when copying. They preserve the `.tcp2shader` ScriptedImporter connection.
- Fonts and icon textures were removed; the editor UI falls back to Unity's built-in styles.
- Shader Generator 2 modules were consolidated into `Shader Templates 2/SG2_Modules.txt`.
- Ramp/gradient creation tools were removed; existing ramp textures can still be assigned manually.
- In URP projects, the `.tcp2shader` importer must compile the shaders while URP is active. This minimal bundle includes an editor sync that automatically reimports `.tcp2shader` assets when their stored render pipeline does not match the current project pipeline.
- For URP outlines, add a Render Objects renderer feature to the active URP Renderer and include the shader pass names `Outline` and `Silhouette`. Make sure its Layer Mask includes the objects that need outlines.
- The generated shader output folder is created automatically when needed.
- This is a minimal functional bundle for Shader Generator 2 plus Hybrid Shader 2, not the full Toony Colors Pro package. Demo scenes, documentation, smoothed meshes, textures, runtime helper components, ramp tools, and upgraders are intentionally not included.
