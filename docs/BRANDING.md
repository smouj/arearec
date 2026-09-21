# AreaRec brand

AreaRec is the capture half of the Area product family. Its visual language is deliberately focused: a four-corner selection frame and a recording-red center dot communicate “record this exact area” at a glance.

## Core assets

| Asset | Use |
| --- | --- |
| `assets/mark.png` | Transparent icon for app surfaces, favicons, and product UI |
| `assets/logo.png` | Dark lockup for README, documentation, and release pages |
| `assets/social-banner.png` | GitHub/social preview banner |
| `assets/AreaRec.ico` | Windows executable and shortcut icon |
| `tools/BuildAppIcon.ps1` | Rebuilds the `.ico` from the transparent mark |

## Tokens

- Deep charcoal: `#0F0F12`
- Warm ivory: `#F4F1EA`
- AreaRec recording red: `#FF3B44`
- Muted graphite: `#25252B`

The red dot is the only saturated accent. Keep the mark on deep charcoal or a neutral light surface with enough contrast. Do not add gradients, shadows, camera silhouettes, or extra controls to the logo.

## Relationship to AreaCut

AreaCut shares the crop-frame geometry but uses a diagonal coral cut mark for editing. AreaRec keeps the center dot and recording red for capture. The two marks should feel like one product family while remaining unambiguous in the Windows taskbar and Start menu.

The transparent mark was generated with Codex's built-in image generation workflow, cleaned for small disconnected artifacts, and used as the deterministic source for the shipped lockup, banner, and icon.
