# CrypkoImageDownloaderCS

this console app downloads crypko's card image using embeded Chromium (CefSharp) off-screen mode.

## usage (single card)
```
usage: CrypkoImageDownloaderCS.exe (cardId)
-o (fileName) : (output)image file name. default: "${cardId}.jpg"
-j (fileName) : (output)json file name. default: none
-t (seconds)  : set timeouts in seconds.

use '-' as fileName to output to STDOUT. 
```

## usage (multiple card)
```
usage: CrypkoImageDownloaderCS.exe --owner (address)
-o (fileName) : (output)image file name. default: "${cardId}.jpg"
-j (fileName) : (output)json file name. default: none
-t (seconds)  : set timeouts in seconds.

use '-' as fileName to output to STDOUT. 

in multiple card mode, the last numeric part in -o,-j is replaced to real card id.
ex) -o data/0.jpg  => data/123456.jpg
```
