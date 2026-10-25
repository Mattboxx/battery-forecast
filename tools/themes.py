"""Genera i dizionari dei temi (python tools/themes.py dalla cartella windows/src/BatteryForecast.App/Themes).

Ogni tema: sfondo, card, testo su tre livelli, accento, curva, fascia, sfumatura della card principale.
Contrasto del testo secondario su card verificato a mano (>= 4.5:1).
"""

THEMES = {
    # nome file: (commento, colori)
    "Dark": ("Crepuscolo: cielo notturno indaco e oro", dict(
        WindowBg="#15162B", WindowBorder="#30315A", CardBg="#1F2040", RowHover="#2A2B52",
        Text="#F3F1F8", TextSecondary="#BBB8CF", TextTertiary="#8F8CA8",
        Accent="#FFC35A", AccentText="#1A1426", Curve="#C7A6F2", Grid="#34355C",
        Reserve="#FF8A7A", Saving="#7FD6A0", Demo="#3B2E14", DemoText="#FFD58A", Focus="#FFFFFF",
        HeroTop="#2A2A57", HeroBottom="#1F2040", HeroBorder="#3A3A6A", Icon="#BBB8CF")),
    "Midnight": ("Notte: quasi nero, adatto agli schermi OLED e alla sera", dict(
        WindowBg="#0B0B10", WindowBorder="#24242E", CardBg="#15151D", RowHover="#1F1F2A",
        Text="#EDEDF2", TextSecondary="#A9A9B8", TextTertiary="#83839A",
        Accent="#F5B04A", AccentText="#120D05", Curve="#9FB4FF", Grid="#26262F",
        Reserve="#FF7A6B", Saving="#6FCF97", Demo="#2C220F", DemoText="#F5C77A", Focus="#FFFFFF",
        HeroTop="#1A1A26", HeroBottom="#13131A", HeroBorder="#2A2A36", Icon="#A9A9B8")),
    "Light": ("Alba: avorio caldo, accento ambra", dict(
        WindowBg="#FBF7F1", WindowBorder="#E6DDD0", CardBg="#FFFFFF", RowHover="#F2EBE1",
        Text="#1E1A16", TextSecondary="#5C554C", TextTertiary="#7A7166",
        Accent="#B45A0A", AccentText="#FFFFFF", Curve="#6A3FA0", Grid="#EDE5DA",
        Reserve="#C23B2A", Saving="#1E7A46", Demo="#FFF1D6", DemoText="#6B4300", Focus="#1E1A16",
        HeroTop="#FFF4E4", HeroBottom="#FFFFFF", HeroBorder="#F0E2CC", Icon="#5C554C")),
    "Mist": ("Nebbia: grigio azzurro neutro, accento blu", dict(
        WindowBg="#F2F4F7", WindowBorder="#DCE1E8", CardBg="#FFFFFF", RowHover="#E7EBF1",
        Text="#161A20", TextSecondary="#545C67", TextTertiary="#6E7682",
        Accent="#2457C5", AccentText="#FFFFFF", Curve="#2F6FD6", Grid="#E3E7EE",
        Reserve="#C2412D", Saving="#167351", Demo="#FFF1D6", DemoText="#6B4300", Focus="#161A20",
        HeroTop="#EAF1FC", HeroBottom="#FFFFFF", HeroBorder="#D8E3F5", Icon="#545C67")),
}

HEAD = '''<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- {comment}. Generato da uno script: tenere le stesse chiavi in tutti i temi. -->
'''

for name, (comment, c) in THEMES.items():
    out = [HEAD.format(comment=comment)]
    for key in ["WindowBg", "WindowBorder", "CardBg", "RowHover", "Text", "TextSecondary", "TextTertiary",
                "Accent", "AccentText", "Curve", "Grid", "Reserve", "Saving", "Demo", "DemoText", "Focus", "Icon"]:
        out.append(f'    <SolidColorBrush x:Key="{key}" Color="{c[key]}"/>\n')
    out.append(f'    <SolidColorBrush x:Key="Band" Color="{c["Curve"]}" Opacity="0.22"/>\n')
    out.append(f'    <Color x:Key="BandColor">{c["Curve"]}</Color>\n')
    out.append(f'    <SolidColorBrush x:Key="HeroBorder" Color="{c["HeroBorder"]}"/>\n')
    out.append(f'''    <LinearGradientBrush x:Key="Hero" StartPoint="0,0" EndPoint="0,1">
        <GradientStop Color="{c["HeroTop"]}" Offset="0"/>
        <GradientStop Color="{c["HeroBottom"]}" Offset="1"/>
    </LinearGradientBrush>
''')
    out.append("</ResourceDictionary>\n")
    open(f"{name}.xaml", "w", encoding="utf-8", newline="\n").write("".join(out))
print("temi:", ", ".join(THEMES))
