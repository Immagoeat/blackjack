# Blackjack — Claude Code Notes

## Project layout

```
Blackjack/
  main file/
    main.cs          – entry point, App class (game loop, input, render dispatch)
    GameLogic.cs     – Card, Deck, Hand, GS (game state), Phase enum
    Renderer.cs      – Font, Renderer (batched quad/text drawing)
    Audio.cs         – AudioEngine (OpenAL + procedural PCM)
    SaveData.cs      – SaveData, SaveSlot (multi-slot persistence)
    main.csproj
  CLAUDE.md
```

## Build & run

```bash
cd "main file"
dotnet build
dotnet run
# or run the binary directly:
./bin/Debug/net9.0/Blackjack
```

## Key architecture decisions

### Rendering
- **Silk.NET.OpenGL 3.3 core** — all drawing goes through `Renderer`, which batches quads
- **Two texture modes**: solid colour (no texture) and font atlas. Switching mode flushes the batch.
- **Coordinate system**: top-left origin, Y increases downward (matches window pixels).
- **Font**: StbTrueTypeSharp bakes ASCII 32–127 at 32 px into a 512×512 atlas. All text calls take `topY` (top of cap-height), not baseline. Unicode suit symbols are NOT in the atlas — draw suits geometrically via `DrawSuit()`.
- **HitRect list**: rebuilt every frame. Mouse click iterates the list and fires the first match.

### Audio
- **Silk.NET.OpenAL** backed by system `libopenal.so`.  No audio files — all sounds are procedurally generated PCM on startup in `AudioEngine.BakeAll()`.
- Music loop is a multi-instrument composition (bass, piano, melody, drums) baked once into a looping OpenAL buffer.
- SFX fire-and-forget: `Play()` spawns a `ThreadPool` item to `DeleteSource` after the buffer duration.

### Saves
- Save directory: `~/.config/blackjack/`  (Linux `ApplicationData`).
- Three named slots: `save0.json`, `save1.json`, `save2.json`.
- `SaveSlot` holds display metadata (name, chips, hands played, last-played timestamp).
- The game starts on a **slot-select screen** (new `Phase.SlotSelect`) before the main menu.

### Animation
- `Card.DealTime` (double, seconds) is stamped when a card is dealt.
- `DrawHandCards` uses `Ease(t)` (cubic ease-out) to slide cards from offscreen.
- Action/results bars slide up after the last card lands (`LastDealTime() + delay`).

### Menu selection
- `GS.MenuSel = -1` means no keyboard highlight. Arrow keys set it; mouse hover drives highlight independently. This prevents PLAY always appearing selected on first load.

## Palette (P static class)

Casino theme: `FeltGreen`, `FeltDark`, `FeltLight`, `Wood*`, `Gold*`, `Red*`, `Green*`, `Blue`, `Purple`, `White`, `Muted`, `Dim`, `Black`, `Card*`, `Chip*`.

## Layout constants (App)

| Name    | Value | Purpose |
|---------|-------|---------|
| HUD_H   | 44    | Top HUD bar height |
| RAIL_H  | 22    | Wood rail height |
| ACT_H   | 96    | Action/result bar height |
| SIDE_W  | 280   | Betting side panel width |

## Gotchas

- `AllowUnsafeBlocks=true` required for STB font baking and GL buffer uploads.
- `EnableDefaultCompileItems=false` — all `.cs` files must be listed in `main.csproj`.
- The font atlas covers only ASCII. Rendering a char outside 32–127 advances by 8 px and draws nothing.
- OpenAL context must be created before any `AL.*` calls. `AudioEngine.Init()` is safe to call even if OpenAL is absent — it catches all exceptions and sets `_ok=false`.
