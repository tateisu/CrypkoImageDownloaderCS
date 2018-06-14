# CrypkoImageDownloaderCS

this console app downloads crypko's card image using embeded Chromium (CefSharp) off-screen mode.

## usage

### get single card

```CrypkoImageDownloaderCS.exe (cardId)```

### get multiple cards by owner

```CrypkoImageDownloaderCS.exe --owner (address)```

## options

```
-o (fileName) : (output)image file name. default: "${cardId}.jpg"
-j (fileName) : (output)json file name. default: none
-t (number)   : set timeouts in seconds.
--user-agent (string) : HTTP User-Agent header. default: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/67.0.3396.79 Safari/537.36"

use '-' as fileName to output to STDOUT. 

in multiple card mode, the last numeric part in -o,-j is replaced to real card id.
ex) -o data/0.jpg  => data/123456.jpg
```

## example

```
$ bin/x86/Release/CrypkoImageDownloaderCS.exe --owner 0xc71dcbcc43ac8bf677c1b7992ddfd0e7bfc464a9 -o data/0.jpg -j data/0.detail.json -t 60
```
