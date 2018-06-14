using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using CefSharp;
using CefSharp.OffScreen;

// usage: CrypkoImageDownloaderCS cardId [-o outfile.jpg]
// default output file name is "{cardId}.jpg".
// use "-o -" to output stdout.

namespace CrypkoImageDownloader
{

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

        private string cardId;
        private string outputFile;

        public MyRequestHandler(string cardId, string outputFile)
        {
            this.cardId = cardId;
            this.outputFile = outputFile;
        }

        private Object mainLock = new Object();
        private long isCompleted = 0;
        private long returnCode = 0;

        public int Run()
        {
            var settings = new CefSettings();
            var multiThreadedMessageLoop = true;
            settings.WindowlessRenderingEnabled = true;
            settings.MultiThreadedMessageLoop = multiThreadedMessageLoop;
            settings.ExternalMessagePump = !multiThreadedMessageLoop;
            Cef.Initialize( settings );

            var browser = new ChromiumWebBrowser( $"https://crypko.ai/#/card/{cardId}" ) {
                RequestHandler = this
            };
            browser.LoadingStateChanged += (object sender, LoadingStateChangedEventArgs args) => {
                Console.Error.WriteLine( "IsLoading=" + args.IsLoading );
            };

            var waitInterval = TimeSpan.FromSeconds( 1.0 );
            var timeout = TimeSpan.FromSeconds( 10.0 );
            var timeStart = DateTime.Now;
            while (Interlocked.Read( ref isCompleted ) == 0) {
                var elapsed = DateTime.Now - timeStart;
                if (elapsed > timeout) {
                    Console.Error.WriteLine( "timeout" );
                    return 1;
                }
                Console.WriteLine( $"waiting {elapsed}/{timeout} seconds…" );
                lock (mainLock) {
                    Monitor.Wait( mainLock, waitInterval );
                }
            }

            //
            Console.Error.WriteLine( "Dispose browser." );
            browser.Dispose();
            
            //
            // Clean up Chromium objects.  You need to call this in your application otherwise
            // you will get a crash when closing.
            Console.Error.WriteLine( "Shutdown Cef." );
            Cef.Shutdown();
            return (int)Interlocked.Read( ref returnCode );
        }

        public void raiseError(long code)
        {
            Interlocked.Exchange( ref returnCode, code );
            Interlocked.Exchange( ref isCompleted, 1L );
            lock (mainLock) {
                Monitor.Pulse( mainLock );
            }
        }

        Dictionary<ulong, MemoryStreamResponseFilter> responseDictionary = new Dictionary<ulong, MemoryStreamResponseFilter>();

        public IResponseFilter makeFilter(IRequest request, ResourceCompleteAction action)
        {
            var dataFilter = new MemoryStreamResponseFilter( action );
            responseDictionary.Add( request.Identifier, dataFilter );
            return dataFilter;
        }

        public Regex reCrypkoDetailApi = new Regex( @"https://api.crypko.ai/crypkos/(\d+)/detail" );
        public Regex reCrypkoImageUrl = new Regex( @"https://img.crypko.ai/daisy/([A-Za-z0-9]+)_lg\.jpg" );

        public string lastCardId = null;

        public override IResponseFilter GetResourceResponseFilter(IWebBrowser browserControl, IBrowser browser, IFrame frame, IRequest request, IResponse response)
        {
            try {
                var uri = request.Url;

                Match m;

                // detect detail API to get card Id
                m = reCrypkoDetailApi.Match( uri );
                if (m.Success) {
                    var cardId = m.Groups[ 1 ].Value;
                    lastCardId = cardId;
                    Console.Error.WriteLine( $"card detail: {cardId} {uri}" );

#if false // may we save detail JSON data if option is specified?
                    return makeFilter( request, (data) => {
                    } );
#endif

                    return null;
                }

                // detect image
                m = reCrypkoImageUrl.Match( uri );
                if (m.Success) {
                    var imageId = m.Groups[ 1 ].Value;
                    Console.Error.WriteLine( $"Image URL: {uri}" );
                    return makeFilter( request, (data) => {
                        if (lastCardId != cardId) {
                            Console.Error.WriteLine( $"card ID not match. expected:{cardId} actually:{lastCardId}" );
                            raiseError( 2 );
                        }
                        if (outputFile == "-") {
                            using (Stream stdout = Console.OpenStandardOutput()) {
                                stdout.Write( data, 0, data.Length );
                            }
                            Console.Error.WriteLine( "Written to Stdout" );
                        } else {
                            File.WriteAllBytes( outputFile, data );
                            Console.Error.WriteLine( $"Saved: {outputFile}" );
                        }
                        raiseError( 0 );
                    } );
                }
            } catch (Exception ex) {
                Console.Error.WriteLine( $"GetResourceResponseFilter: catch exception. {ex}" );
                raiseError( 10 );
            }
            return null;
        }

        public override void OnResourceLoadComplete(IWebBrowser browserControl, IBrowser browser, IFrame frame, IRequest request, IResponse response, UrlRequestStatus status, long receivedContentLength)
        {
            try {
                MemoryStreamResponseFilter filter;
                if (responseDictionary.TryGetValue( request.Identifier, out filter )) {
                    responseDictionary.Remove( request.Identifier );
                    filter.action( filter.Data );
                }
            } catch (Exception ex) {
                Console.Error.WriteLine( $"OnResourceLoadComplete: catch exception. {ex}" );
                raiseError( 11 );
            }
        }
    }


    class Program
    {
        static int Main(string[] args)
        {

            string outputFile = null;
            string cardId = null;
            for (var i = 0; i < args.Length; ++i) {
                System.Diagnostics.Debug.WriteLine( $"{i} {args[ i ]}" );
                var a = args[ i ];
                if (a == "-o") {
                    outputFile = args[ ++i ];
                } else {
                    if (cardId != null) {
                        throw new ArgumentException( "multiple card id is not supported." );
                    }
                    cardId = a;
                }
            }
            if (cardId == null)
                throw new ArgumentException( "usage: CrypkoImageDownloader cardId [-o outfile]" );

            if (outputFile == null)
                outputFile = $"{cardId}.jpg";

            if (outputFile != "-") {
                var dir = Path.GetDirectoryName( outputFile );
                if (dir.Length > 0 && !Directory.Exists( dir )) {
                    Directory.CreateDirectory( dir );
                }
            }

            return new MyRequestHandler( cardId, outputFile ).Run();
        }
    }
}
