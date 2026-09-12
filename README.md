# ClipGlue

Prosty, szybki program do przycinania i sklejania klipów wideo — bez
przekodowywania całego pliku, kiedy tylko się da. Wybierasz pliki,
podajesz zakresy czasu do zachowania (albo zaznaczasz je wizualnie na
osi czasu), a ClipGlue tnie i skleja je jednym poleceniem ffmpeg.

WPF / .NET 8, Windows. Port aplikacji napisanej pierwotnie w Pythonie
(Tkinter), przepisany od zera z tym samym silnikiem ffmpeg pod spodem.

## Funkcje

- Lista plików wideo z zakresami czasu do zachowania na każdym z nich
  (przeciąganie kolejności, wielokrotne zakresy na plik).
- Okno **Trim range** — podgląd wideo z prawdziwym odtwarzaniem, minimapa
  całego pliku, powiększana oś czasu z miniaturkami klatek, przyciąganie
  do klatek kluczowych, zaznaczanie in/out myszą lub klawiaturą.
- **Podgląd całego projektu** w oknie głównym — odtwarza sklejony wynik
  na żywo, bez kodowania niczego na dysk, żeby ocenić cięcia przed
  uruchomieniem właściwego przebiegu.
- Inteligentne cięcie: kopiowanie strumienia (bez rekodowania) tam, gdzie
  się da, przekodowanie tylko fragmentów przyciętych poza klatkami
  kluczowymi.
- Kolejka zadań, pasek postępu, podgląd napisów na klatce.
- `ffmpeg`/`ffprobe` dołączone do programu — nic dodatkowego nie trzeba
  instalować.

## Zrzuty ekranu

**Okno główne** — lista plików z zakresami i podgląd projektu:

![Okno główne ClipGlue](assets/screenshots/main-window.png)

**Trim range** — precyzyjne przycinanie z minimapą, filmstripem i
zaznaczaniem in/out:

![Okno Trim range](assets/screenshots/trim-range.png)

## Wymagania

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) do
  budowania ze źródeł

## Budowanie i uruchomienie

```bash
dotnet build ClipGlue.csproj -c Release
```

Uruchomiony plik `.exe` znajdzie się w
`bin\Release\net8.0-windows\ClipGlue.exe` razem ze skopiowanym
`ffmpeg.exe`/`ffprobe.exe`.

## Jak to działa

1. Dodaj pliki wideo przyciskiem **Add video files**.
2. Wpisz zakresy do zachowania w formacie `HH:MM:SS-HH:MM:SS` albo
   kliknij ▷ przy pliku, żeby otworzyć okno **Trim range** i zaznaczyć je
   wizualnie na osi czasu.
3. Sprawdź całość w **Project preview**, jeśli chcesz zobaczyć sklejkę
   przed uruchomieniem.
4. **START** — program tnie i skleja pliki jednym przebiegiem ffmpeg.
