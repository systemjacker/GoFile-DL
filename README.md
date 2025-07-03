GoFile-DL
    
    GoFile-DL is a powerful command-line interface (CLI) application written in C# for downloading files and folders from GoFile.io. It supports multi-threaded downloads, password-protected content, and allows for exclusion of specific file patterns. The application provides a clean, real-time progress display in the console.

Features

    Download Files & Folders: Download content directly from GoFile.io URLs or content IDs.

    Multi-threaded Downloads: Speed up downloads by utilizing multiple threads for supported files.

    Password Support: Download content protected by a password.

    File Exclusion: Exclude files matching specified patterns (e.g., *.log).

    Clean Console UI: Real-time, non-flickering progress bar and status updates directly in the console, with dynamic resizing support.

    Automatic Token Management: Handles GoFile.io API token and WebsiteToken acquisition automatically.

Setup
Clone the Repository:

    git clone https://github.com/YourUsername/GoFile-DL.git
    cd GoFile-DL

Usage

Run the application from the project's root directory using dotnet run --.
Command-Line Arguments

    <URL_or_ContentID>: (Required) The GoFile.io URL (e.g., https://gofile.io/d/YOUR_ID) or just the content ID (e.g., YOUR_ID) of the file or folder you want to download.

    -t <num_threads>: (Optional) Number of threads to use for downloading. Default is 1.

    -d <output_directory>: (Optional) The directory where files will be saved. Default is ./output.

    -p <password>: (Optional) Password for protected content.

    -e <exclude_pattern>: (Optional) A wildcard pattern to exclude files. Can be used multiple times (e.g., -e "*.log" -e "temp_*").

Examples

    Download a file with default settings:

    dotnet run -- https://gofile.io/d/FileID

    Download a file to a specific directory with 4 threads:

    dotnet run -- https://gofile.io/d/FileID -d "E:\tools\Videos" -t 4

    Download a password-protected folder using its content ID:

    dotnet run -- FileID -d "C:\MyDownloads\Folder" -p "Password"

    Download a folder, excluding .txt files and files starting with _temp_:

    dotnet run -- https://gofile.io/d/someFolderID -d "./downloads" -e "*.txt" -e "_temp_*"

Contributing

    Feel free to open issues or submit pull requests if you have suggestions for improvements or encounter any bugs.
