# Warbox NPC kit

How Warbox (an s&box addon over Sandbox) drives NPCs made by other people, and what an NPC needs to work with it.

- `Example/PlayableSoldier.cs` is the Sandbox soldier, made playable in Warbox: a player can take it over, and it takes RTS orders and turns. Its top comment lists everything Warbox expects from an NPC. Tested in s&box 26.10.27c.
- `Warbox/` is the code from Warbox that finds an NPC's `Warbox*` properties and sets them. It uses other Warbox code that isn't here, so it's for reading, not for building.

The quickest way: give both folders to your AI and ask it to make your NPC compatible.

Made with help of Claude (AI). MIT license.
