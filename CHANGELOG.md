# Changelog

## 1.0.0

First release. Made to be used with Better Flea, but works fine without it.

- Inspect an item and two rows are added under its stats: the Flea Market price and the trader who pays the most. Hover a row for the details. They can go in with the item's stats or on a line of their own, above or below.
- The same prices show under the item's name when you hover it.
- A price tag on every item in your stash and inventory: the flea price, the best trader's, or whichever pays the most (the default). It sits bottom left on a small tag, stepped up past the game's own icons (a part's type icon, a mag's icon, a gun's calibre). Prices read `<1k`, `8.5k`, `37k`, `1.2M`.
- Prices count what's actually on the item: a gun's parts, armour plates, rounds in a magazine, and the condition of every part (durability, key uses, med and fuel left). What's packed inside a rig, bag or case isn't counted.
- Items in a body's or container's pockets get their tag only once they've been searched.
- With Better Flea 2.3.0 or newer on the server, the flea price is Better Flea's own price for that exact item, the same one it judges your listing against. Without it, the flea price is worked out from the base game's flea prices, a gun from its parts. The flea row's tooltip says which one you're looking at.
- The best trader is priced the way their sell screen prices it (attachments, condition, your loyalty level). Dollars and euros are converted to compare. Traders you haven't unlocked are skipped, and Fence can be left out.
- The flea price still shows when you can't list an item there yet. The row reads **Flea market (locked)** and its tooltip gives the reason: flea level, found in raid only, or with Better Flea its bans, item unlock levels and trader-bought items. Until then, only traders will buy it. On the price tag a locked flea price is orange.
- A trader's stock, and lots bigger than a stack can be (a trader's 99999 on the flea), show the price of one item everywhere: the tag, the tooltip and the inspect window.
- Flea prices are kept for two minutes, then asked for again, and everything showing one updates when the new price comes in.
- Price tags cost almost nothing per frame: each one is placed when its item shows, then left alone.
