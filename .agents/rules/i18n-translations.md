---
description: Pravidlo pro automatický překlad všech nových UI textů v Pontish do všech jazykových souborů.
---

# Lokalizace a překlady UI (i18n)

Při přidání nebo úpravě jakéhokoliv viditelného textu v uživatelském rozhraní (`wwwroot/index.html`, `wwwroot/app.js`, případně `src/*.cs`):

1. **Nepoužívej natvrdo zapsané české ani anglické texty** přímo v HTML nebo JS:
   - V `wwwroot/index.html` použij atribut `data-i18n="KlicPrekladu"`.
   - V `wwwroot/app.js` použij funkci `t('KlicPrekladu', ...)`.
2. **Vždy doplň nový klíč do všech jazykových souborů** ve složce `lang/` (aktuálně `lang/cs.json` i `lang/en.json`).
3. **Ověř živé přepnutí jazyka:** při změně jazyka (`case 'state'` / `applyI18n()`) se musí překreslit i dynamicky generované části UI (`renderHome()`, `renderBridges()`, `renderSessionsList()`, `renderTermTabs()`).
