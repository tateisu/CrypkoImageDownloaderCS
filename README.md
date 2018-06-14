# CrypkoImageDownloaderCS

( 日本語はこちら https://github.com/tateisu/CrypkoImageDownloaderCS/blob/master/README-ja.md )

This console app downloads crypko's card image using embeded Chromium (CefSharp) off-screen mode.

Windows-x86 binary is provided in releases page.
https://github.com/tateisu/CrypkoImageDownloaderCS/releases

## Usage

### Get single card

```CrypkoImageDownloaderCS.exe (cardId)```

### Get multiple cards by owner

```CrypkoImageDownloaderCS.exe --owner (address)```

## Options

```
-o (fileName) : (output)The file name of card image. 
                default: "${cardId}.jpg"

-j (fileName) : (output)The file name of card information name. 
                The card information will be saved only when this option is set.

-t (number)   : Set timeouts in seconds. 
                default: 30

--user-agent (string) : HTTP User-Agent header.
                        default: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/67.0.3396.79 Safari/537.36"

Use '-' as fileName to output to STDOUT. 

In multiple card mode, the last numeric part in -o,-j is replaced to real card id.
ex) -o data/0.jpg  => data/123456.jpg
```

## Example

```
$ bin/x86/Release/CrypkoImageDownloaderCS.exe --owner 0xc71dcbcc43ac8bf677c1b7992ddfd0e7bfc464a9 -o data/0.jpg -j data/0.detail.json -t 60
```
