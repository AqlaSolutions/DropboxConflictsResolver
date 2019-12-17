# Dropbox Conflicts Resolver
Resolves dropbox conflicting copies on local computer by comparing LastWriteTime.

* Includes realtime file system monitoring
* Deleted conflicted copies are moved to a recycle folder (default is &lt;DropboxRoot&gt;\..\DropboxConflictsRecycle)

### Usage
DropboxConflictSolver.exe &lt;DropboxRoot&gt; [&lt;RecycleFolder&gt;]
#### Example
DropboxConflictSolver.exe D:\Dropbox
