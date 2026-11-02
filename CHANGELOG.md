# Changelog

## 1.2.3

- Without Better Flea, flea prices are the flea market's own averages again, asked for per item. They were coming from a trader price list that sits well below the flea (a Bundle of wires showed 7,400 instead of about 31,000). Each item's average is kept for 30 minutes, so the server isn't asked again and again.
- Without Better Flea, a gun is priced the way the server prices it when you list it: the flea average for that gun, by its condition. Its attachments are no longer added on top, which counted them twice (an SVD showed about 820k instead of about 410k).
- That trader price list is no longer used anywhere. With Better Flea, a price shows once Better Flea answers, with no early guess. If Better Flea has no price for an item, the flea market's own average is used instead.
- Without Better Flea, your stash and gear are priced as soon as you reach the menu, and flea averages are saved to `BepInEx\cache`, so the next time you play they show straight away while fresh ones load.

## 1.2.2

- A gun with banned ammo loaded, or armour with a banned plate, keeps its flea price and colour instead of falling back to the trader's price. The flea tooltip says to take the banned parts off before listing it.
- New **Colour by price per slot** (off by default): the tier is picked from what the item is worth per slot it takes up, so an item worth 20k that takes 2 slots is coloured as 10k. The price on the item still shows the full price.
- New **Tier 6**, **Tier 7** and **Tier 8**, off by default. Tick one for an extra tier above the others. The defaults are orange from 500k, pink from 1M and cyan from 2M.

## 1.2.1

- New **Highlight flea-banned items** (on by default) and **Flea-banned colour** (red): items the flea has banned get a background of their own colour, instead of a colour from the trader's price. It comes on with Colours, and can be turned off on its own. Only real bans count, not items locked by flea level or found in raid. With Better Flea, its ban list is used; without it, the game's own bans.
- With **Colour item names** on, a flea-banned item's name uses the flea-banned colour too, on the item and in the tooltip.
- Banned ammo keeps its tier colour while **Colour banned ammo by flea price** is on.
- New default **Unsellable colour**: #b48787. Your saved settings aren't changed.

## 1.2.0

### Colours
- New **4. Colours** section in F12: colour items by how much they're worth, with 5 price tiers. Each tier has its own minimum price and a colour picker (click the swatch for sliders, or type a hex code). The defaults run grey, green, blue, purple and gold, from 0 up to 250k.
- Colours are off until you tick **Colours**. Then item backgrounds are coloured, with a strength slider.
- **Colour item names**: on the item, in the hover tooltip, or both.
- **Colour prices**: the price text, a coloured background on the price tag, or both.
- **Colours based on**: the flea price, the best trader's, or whichever pays more.
- Items that can't be sold show in red (**Unsellable colour**).
- Colours stay put as things get used up, each with its own switch: ammo is coloured by what 100 rounds are worth, mags as if fully loaded, money by a full stack, and banned ammo by its flea price so it still ranks by how good it is.
- Roubles, dollars and euros are coloured too. Hover them to see what the stack is worth in roubles.

### Prices
- Prices are ready before you open the stash. Your stash and gear are priced as soon as you reach the menu, and again after each raid, so colours and prices never pop in.
- With Better Flea 2.5.0 or newer, the whole stash is priced in a handful of requests. Items that turn up later are asked for together.
- Without Better Flea, flea prices come from the price list the game already downloads, so they're instant and send no requests.
- **Price on items** set to Most skips a flea price you can't get yet and shows the trader's price, unless no trader buys it.
- New **Price tag size on items** slider.

### Fixes
- Price requests are limited to 20 a second, with hover and inspect first, so a big stash no longer floods the server. This fixes the SSL errors some players saw.
- After 3 failed price requests in a row, requests pause for 10 seconds, then longer up to a minute, so a struggling connection isn't flooded.
- A failed price request is tried again after 10 seconds.
- When a price changes, every item of that kind updates together.
- Redrawing prices is spread over a few frames, so moving, looting or unloading items doesn't stutter.

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
