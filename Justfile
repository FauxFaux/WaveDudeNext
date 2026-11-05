# List the available commands.
[private]
default:
    @just --list

# Restore the portable server solution and install the locked frontend dependencies.
restore: restore-server restore-web

# Restore the server, shared library and API tests without loading WPF.
restore-server:
    dotnet restore WaveDudeNext.Server.slnx -warnaserror

# Restore the portable .NET library without loading the Windows solution.
restore-library:
    dotnet restore WaveDudeNext/WaveDudeNext.csproj -warnaserror

# Install the frontend dependencies from its lockfile.
restore-web:
    npm --prefix web ci

# Build the portable server solution and frontend (run `just restore` on a fresh checkout).
build: build-server build-web

# Build the server, shared library and API tests in Release with zero warnings.
build-server:
    dotnet build WaveDudeNext.Server.slnx -c Release -warnaserror

# Build the portable library in Release with warnings treated as errors.
build-library:
    dotnet build WaveDudeNext/WaveDudeNext.csproj -c Release -warnaserror

# Check TypeScript and build the production frontend.
build-web:
    npm --prefix web run build

# Start the Vite development server.
dev-web:
    npm --prefix web run dev

# Start the workspace API on loopback (Vite proxies /api to port 5080).
dev-server:
    dotnet watch --project WaveDudeNext.Server run --urls http://localhost:5080

# Run the workspace service and HTTP API tests.
test-server:
    dotnet test WaveDudeNext.Server.Tests/WaveDudeNext.Server.Tests.csproj -c Release -warnaserror

# Serve the built frontend for inspection.
preview-web:
    npm --prefix web run preview
