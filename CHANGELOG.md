# Changelog

## 1.2.0 (RC2)

- New **4. Colours** section: colour items by how much they're worth, with 5 price tiers. Each tier has a min price and a colour picker. The defaults run grey, green, blue, purple and gold, from 0 up to 250k. Colours are off until you tick **Colours**, then item backgrounds are coloured.
- **Colour item names**: the item's name on the item, in the hover tooltip, or both.
- Roubles, dollars and euros are coloured by how much the stack is worth. Hover them to see the worth in roubles straight away.
- **Colour prices**: Off, Text (the price on items, in the tooltip and the inspect window), TagBackground (a coloured background on the price on items) or TagBackgroundAndText (both).
- New: **Price tag size on items**, to make the price on items bigger or smaller.
- **Colour item backgrounds**: the background of the item itself, with a strength slider.
- **Colours based on**: the flea price, the best trader's, or whichever pays more.
- Items that can't be sold show in red (**Unsellable colour**): banned on the flea when going by the flea, no trader buys it when going by traders, or both when going by whichever pays more.
- **Price on items** set to Most skips a flea price you can't get yet and shows the trader's price, unless no trader buys it.
- Price requests are limited to 20 a second, so a whole stash redrawing after a raid or a trade no longer floods the server. The item you hover or inspect still goes first.
- Price tags set to Trader no longer ask for flea prices, and nothing asks for flea prices when the flea is turned off on the server.
- A failed price request is tried again after 10 seconds instead of keeping the old price.
- When a price changes, every item of that kind updates together, without moving it.
- Fixed lag spikes from RC1 when items are moved, revealed or unloaded from mags. Only a price that changed redraws every item, and the redraw is spread over a few frames.

## 1.1.0

- The hover tooltip opens straight away instead of after the game's 0.6 second delay.
- Prices start loading as soon as an item shows in a grid, even with price tags off, so they're ready by the time you hover or inspect it. The item you hover or inspect is asked for ahead of the rest.
- New: **Inspect window shows in**, **Hover tooltip shows in** and **Price on items shows in**. Tick Stash, Raid or both for each one. Tick neither to turn it off.
- Removed: **Show in raid** and the **Hover tooltip** on/off switch. The settings above replace them.

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
