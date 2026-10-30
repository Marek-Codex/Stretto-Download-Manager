# Third-party components

Stretto's Apache-2.0 license applies to its original work. It does not replace
the licenses of Microsoft runtimes, SDK components or optional media tools.

The self-contained Windows installer includes the .NET runtime, Windows App SDK
components and System.Security.Cryptography.ProtectedData. Packaging generates
an exact restored-package/runtime notice inventory at this path and copies the
available upstream license and notice files into `third-party-licenses/`.
The generated inventory also labels build dependencies whose notices are
included conservatively; their inclusion does not mean every SDK binary ships.

Windows App SDK components use their supplied Microsoft license terms; they
must not be described collectively as MIT. The .NET runtime and ProtectedData
package have their own accompanying MIT and third-party notices.

ffmpeg and yt-dlp are optional, separately obtained tools, not installer payloads.
Their executables and dependencies retain their upstream terms.
