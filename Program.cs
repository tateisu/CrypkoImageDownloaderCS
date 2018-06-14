using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using CefSharp;
using CefSharp.OffScreen;
using Newtonsoft.Json;

// usage: CrypkoImageDownloaderCS cardId [-o outfile.jpg]
// default output file name is "{cardId}.jpg".
// use "-o -" to output stdout.

namespace CrypkoImageDownloader
{
    public class AppOptions
    {
        public string cardId = null;
        public string outputFile = null;
        public string outputFileOriginal = null;
        public string jsonFile = null;
        public string jsonFileOriginal = null;
        public string crawlOwner = null;
        public string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/67.0.3396.79 Safari/537.36";
        public TimeSpan timeout = TimeSpan.FromSeconds( 30.0 );

        public AppOptions(String[] args)
        {
            for (var i = 0; i < args.Length; ++i) {
                System.Diagnostics.Debug.WriteLine( $"{i} {args[ i ]}" );
                var a = args[ i ];
                if (a == "-o") {
                    outputFile = args[ ++i ];
                } else if (a == "-j") {
                    jsonFile = args[ ++i ];
                } else if (a == "--owner") {
                    crawlOwner = args[ ++i ];
                } else if (a == "--user-agent") {
                    userAgent = args[ ++i ];
                } else if (a == "-t") {
                    timeout = TimeSpan.FromSeconds( Double.Parse( args[ ++i ] ) );
                } else {
                    if (cardId != null) {
                        throw new ArgumentException( "multiple card id is not supported." );
                    }
                    cardId = a;
                }
            }
            if (cardId == null && crawlOwner == null)
                throw new ArgumentException( "usage: CrypkoImageDownloader cardId [-o outfile]" );

            if (outputFile == null) {
                if (cardId == null) {
                    outputFile = "0.jpg"; // will be replaced in eatList()
                } else {
                    outputFile = $"{cardId}.jpg";
                }
            }
                    

            outputFileOriginal = outputFile;
            jsonFileOriginal = jsonFile;

            makeDir( outputFile );
            makeDir( jsonFile );
        }

        void makeDir(string filePath)
        {
            if (filePath == null || filePath == "" || filePath == "-")
                return;
            var dir = Path.GetDirectoryName( filePath );
            if (dir.Length > 0 && !Directory.Exists( dir )) {
                Directory.CreateDirectory( dir );
            }
        }
    }

#pragma warning disable IDE1006 // 命名スタイル
    public class Card
    {
        public long id
        {
            get; set;
        }
    }
    public class SearchResult
    {
        public long totalMatched
        {
            get; set;
        }
        public IList<Card> crypkos
        {
            get; set;
        }

    }
#pragma warning restore IDE1006 // 命名スタイル

    // リソース取得完了時の処理
    public delegate void ResourceCompleteAction(byte[] data);

    // レスポンスボディのフィルタを使って内容をMemoryStreamに保持する
    public class MemoryStreamResponseFilter : IResponseFilter
    {
        public ResourceCompleteAction action;

        public MemoryStreamResponseFilter(ResourceCompleteAction action)
        {
            this.action = action;
        }

        private MemoryStream memoryStream;
        public byte[] Data
        {
            get {
                return memoryStream.ToArray();
            }
        }

        bool IResponseFilter.InitFilter()
        {
            //NOTE: We could initialize this earlier, just one possible use of InitFilter
            memoryStream = new MemoryStream();

            return true;
        }

        FilterStatus IResponseFilter.Filter(Stream dataIn, out long dataInRead, Stream dataOut, out long dataOutWritten)
        {
            if (dataIn == null) {
                dataInRead = 0;
                dataOutWritten = 0;
                return FilterStatus.Done;
            }

            dataInRead = dataIn.Length;
            dataOutWritten = Math.Min( dataInRead, dataOut.Length );

            //Important we copy dataIn to dataOut
            dataIn.CopyTo( dataOut );

            //Copy data to stream
            dataIn.Position = 0;
            dataIn.CopyTo( memoryStream );

            return FilterStatus.Done;
        }

        void IDisposable.Dispose()
        {
            memoryStream.Dispose();
            memoryStream = null;
        }
    }

    public class MyRequestHandler : CefSharp.Handler.DefaultRequestHandler
    {
        public static void log(string line)
        {
            Console.Error.WriteLine( line );
        }

        private static void save(string filePath, byte[] data)
        {
            if (filePath == "-") {
                using (Stream stdout = Console.OpenStandardOutput()) {
                    stdout.Write( data, 0, data.Length );
                }
                log( "Written to Stdout" );
            } else {
                File.WriteAllBytes( filePath, data );
                log( $"Saved: {filePath}" );
            }
        }

        private AppOptions options;

        public MyRequestHandler(AppOptions options)
        {
            this.options = options;
        }

        private Object mainLock = new Object();
        private long isCompleted = 0;
        private long returnCode = 20; // timeout
        private DateTime timeStart = DateTime.Now;
        private DateTime nextPageTime = DateTime.MaxValue;
        private string nextPageUrl = null;

        public int Run()
        {
            if (options.crawlOwner != null) {
                crawlList();
                if (!eatList()) {
                    log( "empty list" );
                    return 1;
                }
            }

            var settings = new CefSettings();
            var multiThreadedMessageLoop = true;
            settings.WindowlessRenderingEnabled = true;
            settings.MultiThreadedMessageLoop = multiThreadedMessageLoop;
            settings.ExternalMessagePump = !multiThreadedMessageLoop;
            settings.LogSeverity = LogSeverity.Error;
            settings.UserAgent = options.userAgent;
            Cef.Initialize( settings );

            var browser = new ChromiumWebBrowser( $"https://crypko.ai/#/card/{options.cardId}" ) {
                RequestHandler = this
            };

            try {
                var waitInterval = TimeSpan.FromSeconds( 1.0 );
                while (Interlocked.Read( ref isCompleted ) == 0) {
                    Interlocked.MemoryBarrier();
                    var now = DateTime.Now;
                    var elapsed = now - timeStart;
                    if (elapsed > options.timeout) {
                        log( "timeout" );
                        return 1;
                    }
                    if (nextPageUrl != null && now >= nextPageTime) {
                        browser.Load( nextPageUrl );
                        nextPageUrl = null;
                        nextPageTime = DateTime.MaxValue;
                        continue;
                    }
                    lock (mainLock) {
                        Monitor.Wait( mainLock, waitInterval );
                    }
                }

                return (int)Interlocked.Read( ref returnCode );

            } finally {
                //
                log( "Dispose browser." );
                browser.Dispose();

                //
                // Clean up Chromium objects.  You need to call this in your application otherwise
                // you will get a crash when closing.
                log( "Shutdown Cef." );
                Cef.Shutdown();
            }
        }

        public void breakLoop(long code)
        {

            if (code == 0) {
                if (eatList()) {
                    timeStart = DateTime.Now;
                    nextPageTime = timeStart + TimeSpan.FromSeconds( 2 );
                    nextPageUrl = $"https://crypko.ai/#/card/{options.cardId}";
                    lock (mainLock) {
                        Monitor.Pulse( mainLock );
                    }
                    return;
                }
            }

            Interlocked.Exchange( ref returnCode, code );
            Interlocked.Exchange( ref isCompleted, 1L );
            lock (mainLock) {
                Monitor.Pulse( mainLock );
            }
        }

        List<string> cardIdList = new List<string>();

        private void crawlList()
        {
            SortedSet<String> tmpSet = new SortedSet<String>();
            try {
                using (WebClient client = new WebClient()) {

                    client.Headers.Set( HttpRequestHeader.UserAgent, options.userAgent );
                    client.Headers.Set( HttpRequestHeader.Accept, "application/json, text/plain, */*" );
                    client.Headers.Set( HttpRequestHeader.Referer, "https://crypko.ai/" );
                    client.Headers.Set( HttpRequestHeader.AcceptLanguage, "ja-JP,ja;q=0.9,en-US;q=0.8,en;q=0.7" );
                    // client.Headers.Set( HttpRequestHeader.AcceptEncoding, "gzip, deflate" );

                    for (var page = 1; page < 1000; ++page) {
                        var url = $"https://api.crypko.ai/crypkos/search?category=all&sort=-id&ownerAddr={options.crawlOwner}" + ( page > 1 ? $"&page={page}" : "" );
                        log( $"get {url}" );
                        for (var retry = 0; retry < 10; ++retry) {

                            // to avoid rate-limit, sleep before each request
                            Thread.Sleep( 1500 );

                            string jsonString = null;
                            try {
                                // client.DownloadString を使うと文字化けしてJSONパースに失敗することがある
                                var jsonBytes = client.DownloadData( url );
                                jsonString = System.Text.Encoding.UTF8.GetString( jsonBytes );

                                var searchResult = JsonConvert.DeserializeObject<SearchResult>( jsonString );
                                var crypkos = searchResult.crypkos;
                                log( $"page={page} count={tmpSet.Count}/{searchResult.totalMatched}, page contains {crypkos.Count} cards" );
                                if (crypkos.Count == 0) {
                                    log( "end of list deteted." );
                                    return;
                                }
                                foreach (var card in crypkos) {
                                    var cardId = $"{card.id}";
                                    tmpSet.Add( cardId );
                                }

                                break;
                            } catch (Newtonsoft.Json.JsonReaderException ex) {
                                // JSONパースに失敗した場合はリトライしない
                                log( $"{ex}" );
                                if (jsonString != null)
                                    log( jsonString );
                                tmpSet.Clear();
                                return;
                            } catch (Exception ex) {
                                log( $"{ex}" );
                                if (jsonString != null)
                                    log( jsonString );
                            }
                        }
                    }
                }
            } finally {
                cardIdList = new List<string>( tmpSet );
            }

        }

        private bool eatList()
        {
            try {
                while (cardIdList.Count > 0) {

                    var cardId = cardIdList[ 0 ];
                    cardIdList.RemoveAt( 0 );

                    options.outputFile = Regex.Replace( options.outputFileOriginal, @"(\d+)(\D*)$", $"{cardId}$2" );
                    if (File.Exists( options.outputFile )) {
                        log( $"skip {cardId}, already exists {options.outputFile}" );
                        continue;
                    }

                    if (options.jsonFileOriginal != null) {
                        options.jsonFile = Regex.Replace( options.jsonFileOriginal, @"(\d+)(\D*)$", $"{cardId}$2" );
                    }

                    options.cardId = cardId;
                    return true;
                }
            } catch (Exception ex) {
                log( $"{ex}" );
            }
            return false;
        }

        Dictionary<ulong, MemoryStreamResponseFilter> filterMap = new Dictionary<ulong, MemoryStreamResponseFilter>();

        public IResponseFilter makeFilter(IRequest request, ResourceCompleteAction action)
        {
            var dataFilter = new MemoryStreamResponseFilter( action );
            filterMap.Add( request.Identifier, dataFilter );
            return dataFilter;
        }

        // リソースのロード完了時にフィルタ完了アクションを実行する
        public override void OnResourceLoadComplete(IWebBrowser browserControl, IBrowser browser, IFrame frame, IRequest request, IResponse response, UrlRequestStatus status, long receivedContentLength)
        {
            try {
                MemoryStreamResponseFilter filter;
                if (filterMap.TryGetValue( request.Identifier, out filter )) {
                    filterMap.Remove( request.Identifier );
                    filter.action( filter.Data );
                }
            } catch (Exception ex) {
                log( $"OnResourceLoadComplete: catch exception. {ex}" );
                breakLoop( 11 );
            }
        }


        public Regex reCrypkoDetailApi = new Regex( @"https://api.crypko.ai/crypkos/(\d+)/detail" );
        public Regex reCrypkoImageUrl = new Regex( @"https://img.crypko.ai/daisy/([A-Za-z0-9]+)_lg\.jpg" );

        public string lastCardId = null;

        public override IResponseFilter GetResourceResponseFilter(IWebBrowser browserControl, IBrowser browser, IFrame frame, IRequest request, IResponse response)
        {
            try {
                // detail API はOPTIONSとGETの2回呼び出される。
                // フィルタするのはGETの時だけ
                if (request.Method != "GET")
                    return null;

                var uri = request.Url;

                Match m;

                // detect detail API to get card Id
                m = reCrypkoDetailApi.Match( uri );
                if (m.Success) {
                    var cardId = m.Groups[ 1 ].Value;
                    lastCardId = cardId;
                    log( $"card detail: {cardId} {uri}" );
                    if (options.jsonFile != null) {
                        return makeFilter( request, (data) => {
                            save( options.jsonFile, data );
                        } );
                    }
                    return null;
                }

                // detect image
                m = reCrypkoImageUrl.Match( uri );
                if (m.Success) {
                    var imageId = m.Groups[ 1 ].Value;
                    log( $"Image URL: {uri}" );
                    return makeFilter( request, (data) => {
                        if (lastCardId != options.cardId) {
                            log( $"card ID not match. expected:{options.cardId} actually:{lastCardId}" );
                            breakLoop( 2 );
                        }
                        save( options.outputFile, data );

                        breakLoop( 0 );
                    } );
                }
            } catch (Exception ex) {
                log( $"GetResourceResponseFilter: catch exception. {ex}" );
                breakLoop( 10 );
            }
            return null;
        }



    }


    class Program
    {
        static int Main(string[] args)
        {
            var options = new AppOptions( args );
            return new MyRequestHandler( options ).Run();
        }
    }
}
