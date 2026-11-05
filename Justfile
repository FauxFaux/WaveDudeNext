# List the available commands.
[private]
default:
    @just --list

# Restore the portable library and install the locked frontend dependencies.
restore: restore-library restore-web

# Restore the portable .NET library without loading the Windows solution.
restore-library:
    dotnet restore WaveDudeNext/WaveDudeNext.csproj -warnaserror

# Install the frontend dependencies from its lockfile.
restore-web:
    npm --prefix web ci

# Build the portable library and frontend (run `just restore` on a fresh checkout).
build: build-library build-web

# Build the portable library in Release with warnings treated as errors.
build-library:
    dotnet build WaveDudeNext/WaveDudeNext.csproj -c Release -warnaserror

# Check TypeScript and build the production frontend.
build-web:
    npm --prefix web run build

# Start the Vite development server.
dev-web:
    npm --prefix web run dev

# Serve the built frontend for inspection.
preview-web:
    npm --prefix web run preview
