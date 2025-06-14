// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
using Duplicati.Library.Common.IO;
using Duplicati.Library.Interface;
using Duplicati.Library.Utility;
using Duplicati.Library.Utility.Options;
using System.Runtime.CompilerServices;

namespace Duplicati.Library.Backend
{
    // ReSharper disable once UnusedMember.Global
    // This class is instantiated dynamically in the BackendLoader.
    public class Dropbox : IBackend, IStreamingBackend
    {
        private static readonly string TOKEN_URL = AuthIdOptionsHelper.GetOAuthLoginUrl("dropbox", null);
        private readonly string m_path;
        private readonly DropboxHelper dbx;

        // ReSharper disable once UnusedMember.Global
        // This constructor is needed by the BackendLoader.
        public Dropbox()
        {
            m_path = null!;
            dbx = null!;
        }

        // ReSharper disable once UnusedMember.Global
        // This constructor is needed by the BackendLoader.
        public Dropbox(string url, Dictionary<string, string?> options)
        {
            var uri = new Utility.Uri(url);

            m_path = Utility.Uri.UrlDecode(uri.HostAndPath);
            if (m_path.Length != 0 && !m_path.StartsWith("/", StringComparison.Ordinal))
                m_path = "/" + m_path;

            if (m_path.EndsWith("/", StringComparison.Ordinal))
                m_path = m_path.Substring(0, m_path.Length - 1);

            var authId = AuthIdOptionsHelper.Parse(options);
            authId.RequireCredentials(TOKEN_URL);

            dbx = new DropboxHelper(authId, TimeoutOptionsHelper.Parse(options));
        }

        public void Dispose()
        {
            // do nothing
        }

        public string DisplayName => Strings.Dropbox.DisplayName;

        public string ProtocolKey => "dropbox";

        private IFileEntry ParseEntry(MetaData md)
        {
            var ife = new FileEntry(md.name);
            if (md.IsFile)
            {
                ife.IsFolder = false;
                ife.Size = (long)md.size;
            }
            else
            {
                ife.IsFolder = true;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(md.server_modified))
                    ife.LastModification = ife.LastAccess = DateTime.Parse(md.server_modified).ToUniversalTime();
            }
            catch { }

            return ife;
        }

        private void ThrowFolderMissingException(DropboxException de)
        {
            if (de.errorJSON?["error"]?[".tag"]?.ToString() == "path" && de.errorJSON?["error"]?["path"]?[".tag"]?.ToString() == "not_found")
                throw new FolderMissingException();
        }

        private async Task<T> HandleListExceptions<T>(Func<Task<T>> func)
        {
            try
            {
                return await func().ConfigureAwait(false);
            }
            catch (DropboxException de)
            {
                ThrowFolderMissingException(de);
                throw;
            }
        }

        public async IAsyncEnumerable<IFileEntry> ListAsync([EnumeratorCancellation] CancellationToken cancelToken)
        {
            var lfr = await HandleListExceptions(() => dbx.ListFiles(m_path, cancelToken)).ConfigureAwait(false);

            foreach (var md in lfr.entries ?? [])
                yield return ParseEntry(md);

            while (lfr.has_more)
            {
                lfr = await HandleListExceptions(() => dbx.ListFilesContinue(lfr.cursor!, cancelToken)).ConfigureAwait(false);
                foreach (var md in lfr.entries ?? [])
                    yield return ParseEntry(md);
            }
        }

        public async Task PutAsync(string remotename, string filename, CancellationToken cancelToken)
        {
            using (var fs = File.OpenRead(filename))
                await PutAsync(remotename, fs, cancelToken).ConfigureAwait(false);
        }

        public async Task GetAsync(string remotename, string filename, CancellationToken cancelToken)
        {
            using (var fs = File.Create(filename))
                await GetAsync(remotename, fs, cancelToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string remotename, CancellationToken cancelToken)
        {
            try
            {
                var path = $"{m_path}/{remotename}";
                await dbx.DeleteAsync(path, cancelToken).ConfigureAwait(false);
            }
            catch (DropboxException de)
            {
                ThrowFolderMissingException(de);
                throw;
            }
        }

        public IList<ICommandLineArgument> SupportedCommands =>
        [
            .. AuthIdOptionsHelper.GetOptions(TOKEN_URL),
            .. TimeoutOptionsHelper.GetOptions(),
        ];

        public string Description { get { return Strings.Dropbox.Description; } }

        public Task<string[]> GetDNSNamesAsync(CancellationToken cancelToken) => Task.FromResult(WebApi.Dropbox.Hosts());

        public Task TestAsync(CancellationToken cancelToken)
            => this.TestReadWritePermissionsAsync(cancelToken);

        public async Task CreateFolderAsync(CancellationToken cancelToken)
        {
            try
            {
                await dbx.CreateFolderAsync(m_path, cancelToken).ConfigureAwait(false);
            }
            catch (DropboxException de)
            {

                if (de.errorJSON?["error"]?[".tag"]?.ToString() == "path" && de.errorJSON["error"]?["path"]?[".tag"]?.ToString() == "conflict")
                    throw new FolderAreadyExistedException();
                throw;
            }
        }

        public async Task PutAsync(string remotename, Stream stream, CancellationToken cancelToken)
        {
            try
            {
                string path = $"{m_path}/{remotename}";
                await dbx.UploadFileAsync(path, stream, cancelToken);
            }
            catch (DropboxException de)
            {
                ThrowFolderMissingException(de);
                throw;
            }
        }

        public async Task GetAsync(string remotename, Stream stream, CancellationToken cancelToken)
        {
            try
            {
                string path = string.Format("{0}/{1}", m_path, remotename);
                await dbx.DownloadFileAsync(path, stream, cancelToken).ConfigureAwait(false);
            }
            catch (DropboxException de)
            {
                ThrowFolderMissingException(de);
                throw;
            }
        }
    }
}
