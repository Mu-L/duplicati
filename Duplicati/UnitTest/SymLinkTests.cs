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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Duplicati.Library.Interface;
using Duplicati.Library.Main;
using Duplicati.Library.Snapshots;
using NUnit.Framework;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace Duplicati.UnitTest
{
    [TestFixture]
    public class SymLinkTests : BasicSetupHelper
    {
        [Test]
        [Category("SymLink")]
        public void SymlinkExists()
        {
            // Create symlink target directory
            const string targetDirName = "target";
            var targetDir = systemIO.PathCombine(this.DATAFOLDER, targetDirName);
            systemIO.DirectoryCreate(targetDir);
            // Create files in symlink target directory
            var fileNames = new[] { "a.txt", "b.txt", "c.txt" };
            foreach (var file in fileNames)
            {
                var targetFile = systemIO.PathCombine(targetDir, file);
                TestUtils.WriteFile(targetFile, Encoding.Default.GetBytes(file));
            }

            // Create actual symlink directory linking to the target directory
            const string symlinkDirName = "symlink";
            var symlinkDir = systemIO.PathCombine(this.DATAFOLDER, symlinkDirName);
            try
            {
                systemIO.CreateSymlink(symlinkDir, targetDir, asDir: true);
            }
            catch (Exception e)
            {
                // If client cannot create symlinks, mark test as ignored
                Assert.Ignore($"Client could not create a symbolic link. Error reported: {e.Message}");
            }

            // Backup all files
            Dictionary<string, string> restoreOptions = new(this.TestOptions) { ["restore-path"] = this.RESTOREFOLDER };
            using (Controller c = new("file://" + this.TARGETFOLDER, this.TestOptions, null))
            {
                IBackupResults backupResults = c.Backup(new[] { this.DATAFOLDER });
                Assert.AreEqual(0, backupResults.Errors.Count());
                Assert.AreEqual(0, backupResults.Warnings.Count());
            }

            // Restore all files
            using (Controller c = new("file://" + this.TARGETFOLDER, restoreOptions, null))
            {
                IRestoreResults restoreResults = c.Restore(null);
                Assert.AreEqual(0, restoreResults.Errors.Count());
                Assert.AreEqual(0, restoreResults.Warnings.Count());

                // Verify that symlink was restored
                var restoreSymlinkDir = systemIO.PathCombine(this.RESTOREFOLDER, symlinkDirName);
                Assert.That(systemIO.IsSymlink(restoreSymlinkDir), Is.True);
                var restoredSymlinkFullPath = systemIO.PathGetFullPath(systemIO.GetSymlinkTarget(restoreSymlinkDir));
                var symlinkTargetFullPath = systemIO.PathGetFullPath(targetDir);
                Assert.That(restoredSymlinkFullPath, Is.EqualTo(symlinkTargetFullPath));
            }

            // Restore again, trying to overwrite existing symlink
            using (Controller c = new("file://" + this.TARGETFOLDER, restoreOptions, null))
            {
                IRestoreResults restoreResults = c.Restore(null);
                Assert.AreEqual(0, restoreResults.Errors.Count());
                Assert.AreEqual(0, restoreResults.Warnings.Count());
            }
        }

        [Test]
        [Category("SymLink")]
        [TestCase(Options.SymlinkStrategy.Store)]
        [TestCase(Options.SymlinkStrategy.Follow)]
        [TestCase(Options.SymlinkStrategy.Ignore)]
        public void SymLinkPolicy(Options.SymlinkStrategy symlinkPolicy)
        {
            // Create symlink target directory
            const string targetDirName = "target";
            var targetDir = systemIO.PathCombine(this.DATAFOLDER, targetDirName);
            systemIO.DirectoryCreate(targetDir);
            // Create files in symlink target directory
            var fileNames = new[] { "a.txt", "b.txt", "c.txt" };
            foreach (var file in fileNames)
            {
                var targetFile = systemIO.PathCombine(targetDir, file);
                TestUtils.WriteFile(targetFile, Encoding.Default.GetBytes(file));
            }

            // Create actual symlink directory linking to the target directory
            const string symlinkDirName = "symlink";
            var symlinkDir = systemIO.PathCombine(this.DATAFOLDER, symlinkDirName);
            try
            {
                systemIO.CreateSymlink(symlinkDir, targetDir, asDir: true);
            }
            catch (Exception e)
            {
                // If client cannot create symlinks, mark test as ignored
                Assert.Ignore($"Client could not create a symbolic link. Error reported: {e.Message}");
            }

            // Backup all files with given symlink policy
            Dictionary<string, string> restoreOptions = new Dictionary<string, string>(this.TestOptions) { ["restore-path"] = this.RESTOREFOLDER };
            Dictionary<string, string> backupOptions = new Dictionary<string, string>(this.TestOptions) { ["symlink-policy"] = symlinkPolicy.ToString() };
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, backupOptions, null))
            {
                IBackupResults backupResults = c.Backup(new[] { this.DATAFOLDER });
                Assert.AreEqual(0, backupResults.Errors.Count());
                Assert.AreEqual(0, backupResults.Warnings.Count());
            }
            // Restore all files
            using (Controller c = new Controller("file://" + this.TARGETFOLDER, restoreOptions, null))
            {
                IRestoreResults restoreResults = c.Restore(null);
                Assert.AreEqual(0, restoreResults.Errors.Count());
                Assert.AreEqual(0, restoreResults.Warnings.Count());

                // Verify that symlink policy was followed
                var restoreSymlinkDir = systemIO.PathCombine(this.RESTOREFOLDER, symlinkDirName);
                switch (symlinkPolicy)
                {
                    case Options.SymlinkStrategy.Store:
                        // Restore should contain an actual symlink to the original target
                        Assert.That(systemIO.IsSymlink(restoreSymlinkDir), Is.True);
                        var restoredSymlinkFullPath = systemIO.PathGetFullPath(systemIO.GetSymlinkTarget(restoreSymlinkDir));
                        var symlinkTargetFullPath = systemIO.PathGetFullPath(targetDir);
                        Assert.That(restoredSymlinkFullPath, Is.EqualTo(symlinkTargetFullPath));
                        break;
                    case Options.SymlinkStrategy.Follow:
                        // Restore should contain a regular directory with copies of the files in the symlink target
                        Assert.That(systemIO.IsSymlink(restoreSymlinkDir), Is.False);
                        TestUtils.AssertDirectoryTreesAreEquivalent(targetDir, restoreSymlinkDir, true, "Restore");
                        break;
                    case Options.SymlinkStrategy.Ignore:
                        // Restore should not contain the symlink or directory at all
                        Assert.That(systemIO.DirectoryExists(restoreSymlinkDir), Is.False);
                        Assert.That(systemIO.FileExists(restoreSymlinkDir), Is.False);
                        break;
                    default:
                        Assert.Fail($"Unexpected symlink policy");
                        break;
                }
            }
        }
    }
}
