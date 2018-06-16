# CrypkoImageDownloaderCS

(in English: https://github.com/tateisu/CrypkoImageDownloaderCS/blob/master/README.md )

crypkoのカード画像をダウンロードするdot-NET コンソールアプリです。

Windows用の実行ファイルはリリースのページに添付されています。
https://github.com/tateisu/CrypkoImageDownloaderCS/releases

## 使い方

### カード1枚を取得

```CrypkoImageDownloaderCS.exe (cardId)```

### 複数のカードを取得(オーナー指定)

```CrypkoImageDownloaderCS.exe --owner (address)```

### 複数のカードを取得(いいね指定)

```CrypkoImageDownloaderCS.exe --like-by (address)```

## オプション

```
-o (ファイル名) : (出力)カード画像のファイル名。
                  デフォルト："{カードID}.jpg"

-j (ファイル名) : (出力)カード情報のファイル名。
                  このオプションを指定した時だけカード情報を保存します。

-t (数値) : タイムアウトを秒数で指定します。
            デフォルト: 30

-v : ログ出力を冗長にします。

--user-agent (string) : HTTPのUser-Agentヘッダを指定します。
                        デフォルト: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/67.0.3396.79 Safari/537.36"

ファイル名に「-」を指定すると標準出力が使われます。

複数カードモードでは、-o,-j のファイル名の最後の数字列部分は実際のカードIDに置き換えられます。
例) -o data/0.jpg  => data/123456.jpg
```

## 使用例

```CrypkoImageDownloaderCS.exe --owner 0xc71dcbcc43ac8bf677c1b7992ddfd0e7bfc464a9 -o data/0.jpg -j data/0.detail.json -t 60```

オーナーのアドレスはあなたのアドレスに書き換えてください

