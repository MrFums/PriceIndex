# Changelog

## 1.0.0

First release.

- Inspect an item and two rows are added under its stats: the Flea Market price and the trader who pays the most. Hover a row for the details. They can go in with the item's stats or on a line of their own, above or below.
- The same prices show under the item's name when you hover it.
- Optional price tag on every item in your stash and inventory: the flea price, the best trader's, or whichever pays the most. It sits bottom left on a small tag, stepped up past the game's own icons (a part's type icon, a mag's icon, a gun's calibre). Prices read `<1k`, `8.5k`, `37k`, `1.2M`.
- Prices count what's actually on the item: a gun's parts, armour plates, rounds in a magazine, and the condition of every part (durability, key uses, med and fuel left). What's packed inside a rig, bag or case isn't counted.
- A trader's stock always shows the price of one item, not the whole stack.
- Items in a body's or container's pockets get their tag only once they've been searched.
- With Better Flea on the server, the flea price is Better Flea's own price for that exact item, the same one it judges your listing against. Without it, the flea price is worked out from the base game's flea prices, a gun from its parts. The flea row's tooltip says which one you're looking at.
- The best trader is priced the way their sell screen prices it (attachments, condition, your loyalty level). Dollars and euros are converted to compare. Traders you haven't unlocked are skipped, and Fence can be left out.
- Items that can't go on the flea say so, with the reason (flea level, found in raid only, and Better Flea's bans and trader-bought items).
- Flea prices are kept for two minutes, then asked for again, and everything showing one updates when the new price comes in.
- Price tags cost almost nothing per frame: each one is placed when its item shows, then left alone.
