APP ICON FILE

When your logo is ready, export it as a Windows .ico file named exactly:
NovaScreenRecorder-Icon.ico

Put that .ico file in this same folder, beside NovaScreenRecorder.App.csproj.
The GitHub Actions workflow will embed it as the executable's Windows file icon on the next successful build.

This text file is only instructions; it is not the icon itself. The app builds without an icon until you add the .ico file.
