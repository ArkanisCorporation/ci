export function formatGreeting(projectName: string): string {
    const normalized = projectName.trim();
    if (normalized.length === 0) {
        throw new Error("project name is required");
    }

    const displayName = normalized
        .split(/\s+/)
        .map((part) => part[0]!.toUpperCase() + part.slice(1).toLowerCase())
        .join(" ");

    return `Hello, ${displayName}!`;
}
