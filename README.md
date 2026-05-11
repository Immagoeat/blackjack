# Casino Night

A real-time casino game built with **C# + OpenGL** (Silk.NET). No game engine — everything from card animations to slot reels is rendered with a custom batched quad renderer and a hand-baked font atlas.

---

## Games

### Blackjack
Classic 6-deck Vegas rules. Hit, stand, double down, split pairs. Hot-seat multiplayer for 2–4 players. Full animated card deals with cubic ease-out sliding from the deck.

### Texas Hold'em
Full no-limit Texas Hold'em with Pre-Flop → Flop → Turn → River → Showdown phases.
- **vs AI** — up to 5 AI opponents with hand-strength evaluation and preflop/postflop betting logic
- **Local hot-seat** — 2–4 human players on the same machine
- **LAN multiplayer** — host a game on your network; friends join by IP. Host runs all game logic; state is synced to all clients with hole cards masked until showdown. Port **27015** (TCP).

Community cards animate in with staggered deals. Phase transitions (FLOP / TURN / RIVER) flash gold when cards land.

### Slots
3-reel weighted slot machine with 8 symbol types.

| Combination | Multiplier |
|---|---|
| 💎 Diamond Diamond Diamond | 500× |
| 7 7 7 | 100× |
| BAR BAR BAR | 50× |
| 🔔 Bell Bell Bell | 20× |
| Plum Plum Plum | 10× |
| Orange Orange Orange | 8× |
| Lemon Lemon Lemon | 5× |
| Cherry Cherry Cherry | 3× |
| Cherry Cherry | 2× |
| Cherry | 1× |

Symbols are drawn geometrically with multi-layer shading and shine highlights — no sprite sheets.

---

## Build & Run

**Requirements:** .NET 9 SDK, OpenAL (system library)

```bash
cd "main file"
dotnet build
dotnet run
```

Or run the binary directly:
```bash
./main\ file/bin/Debug/net9.0/Blackjack
```

---

## Architecture

| File | Purpose |
|---|---|
| `main.cs` | App class — game loop, input, all rendering |
| `GameLogic.cs` | Card, Deck, Hand, GS (game state), Phase/GameMode enums |
| `PokerLogic.cs` | Full Texas Hold'em engine — hand eval, AI, blinds, streets |
| `SlotsLogic.cs` | Weighted reel strip, spin animation, pay table |
| `LanNetwork.cs` | TCP host/client, game state snapshots, action relay |
| `SaveData.cs` | 3-slot save system (`~/.config/blackjack/`) |
| `Audio.cs` | Procedural PCM via OpenAL — music loop + SFX, no audio files |

### Rendering
- **Silk.NET.OpenGL 3.3 core** — all drawing goes through a batched quad renderer
- **StbTrueTypeSharp** — ASCII 32–127 baked into a 512×512 font atlas at 32 px
- Suits and slot symbols are drawn geometrically with scanline fills; no textures beyond the font atlas
- HitRect list rebuilt every frame; clicks fire the first matching rect

### Audio
All sounds are procedurally generated PCM at startup — multi-instrument music loop (bass, piano, melody, drums) baked into a looping OpenAL buffer, plus fire-and-forget SFX.

---

## Save Slots

Three named save slots stored in `~/.config/blackjack/`. Each slot persists chip count, hands played, win count, and net winnings. Slots can be deleted from the slot-select screen.

---

## Controls

### Blackjack
| Key | Action |
|---|---|
| H | Hit |
| S | Stand |
| D | Double Down |
| P | Split |
| Mouse | All menu/bet interactions |

### Texas Hold'em
| Key | Action |
|---|---|
| F | Fold |
| C | Check / Call |
| R | Raise |
| A | All-In |
| +/- | Adjust raise size |

### Slots
| Key / Button | Action |
|---|---|
| Space | Spin |
| Mouse | Bet adjustments |
