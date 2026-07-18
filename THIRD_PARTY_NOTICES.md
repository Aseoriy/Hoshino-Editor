# Third-party notices

Hoshino Editor uses or distributes the third-party software listed below. These components retain their own copyright and license terms. The MIT License for Hoshino Editor does not replace or override those terms.

## FFmpeg

Release installers bundle `ffmpeg.exe` as a separate command-line program for speed-adjusted and upscaled video export.

- Project: FFmpeg
- Website: <https://ffmpeg.org>
- Bundled build: FFmpeg 8.1.2 Essentials Build for Windows by Gyan Doshi
- Build information: <https://www.gyan.dev/ffmpeg/builds/>
- Corresponding FFmpeg source revision: <https://github.com/FFmpeg/FFmpeg/tree/38b88335f99e76ed89ff3c93f877fdefce736c13>
- Source archive: <https://github.com/FFmpeg/FFmpeg/archive/38b88335f99e76ed89ff3c93f877fdefce736c13.zip>
- License: GNU General Public License version 3 or later
- License text: <https://www.gnu.org/licenses/gpl-3.0.html>

The bundled Gyan Windows build is a static GPLv3 build. Hoshino invokes the executable as a separate process and does not incorporate FFmpeg source code into the Hoshino source tree. A copy of these notices is installed beside the application. The installer build also creates `ffmpeg-8.1.2-source-38b88335f9.zip` for publication beside the release binary.

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project. Hoshino Editor and Sail Solutions are not affiliated with or endorsed by the FFmpeg project or the build distributor.

## Microsoft .NET

Self-contained releases include portions of the Microsoft .NET runtime and Windows Presentation Foundation so users do not need a separate .NET installation.

- .NET runtime source: <https://github.com/dotnet/runtime>
- WPF source: <https://github.com/dotnet/wpf>
- License: MIT
- License text: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>

Microsoft, .NET, Windows, and related names may be trademarks of Microsoft Corporation. Hoshino Editor and Sail Solutions are not affiliated with or endorsed by Microsoft.
