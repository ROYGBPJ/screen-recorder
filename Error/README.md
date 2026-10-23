# Error log folder

The app creates its real runtime log folder at `%LOCALAPPDATA%\NovaScreenRecorder\Error` so it can write logs without administrator permissions and without attempting to write inside `Program Files`.

Daily `Nova-Error-YYYY-MM-DD.log` files contain recorded application errors and FFmpeg error output. Logs are local-only and retained indefinitely until the user manually deletes them. The app includes a **Keep error logs on this PC** option to stop collecting new logs; turning it off does not erase previous logs.
