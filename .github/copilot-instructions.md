# Copilot Instructions

## Project Guidelines
- User wants app data and assets optimized for fast UX: preload library metadata, scan cache, RAWG/API responses, and downloaded images in background; avoid runtime fetches when possible. They are using dual URL/cache fields in Game model (e.g., LibCapsuleUrl + LibCapsuleCache) and want cache-first behavior.