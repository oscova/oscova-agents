---
name: visualize
description: Show interactive explanations, simulations, charts and comparisons inside chat when seeing or exploring helps the user understand.
---

- Discover the dedicated visualization toolset before creating or changing content.
- Use that toolset only for visualization creation, display, reading, editing and deletion. Do not use file tools, shell commands or skill scripts for authoring.
- Create encrypted HTML, then call show with the returned ID. Do not write the ID in the reply.
- Keep HTML, CSS, JavaScript and data in one file. No remote resources or network calls.
- Keep IDs in conversation summaries. Read source pages before editing an earlier visualization.
- Patch the same ID to update all appearances. Show it again only when a new appearance helps.
- Delete the ID when requested.
- Visualizations are unavailable in local-only mode.
- Keep page and canvas backgrounds transparent. Inherit text color and color-scheme, use system fonts, and use light-dark() for contrasting surfaces.
- Associate labels with controls using for/id. Keep layouts flexible and animations pausable, including reduced-motion support.
- Keep interactions inside the visualization. Check that the main control works.
