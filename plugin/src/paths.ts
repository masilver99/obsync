export function conflictPath(path: string, label: string, now = new Date(), exists: (candidate: string) => boolean = () => false): string {
  const slash = path.lastIndexOf("/");
  const directory = slash >= 0 ? path.slice(0, slash + 1) : "";
  const fileName = slash >= 0 ? path.slice(slash + 1) : path;
  const dot = fileName.lastIndexOf(".");
  const stem = dot > 0 ? fileName.slice(0, dot) : fileName;
  const extension = dot > 0 ? fileName.slice(dot) : "";
  const safeLabel = label.replace(/[^a-zA-Z0-9_-]/g, "") || "device";
  const stamp = now.toISOString().replace(/[-:]/g, "").replace(/\.\d{3}Z$/, "Z");
  const base = `${directory}${stem} (conflict ${safeLabel} ${stamp})${extension}`;
  if (!exists(base)) {
    return base;
  }

  for (let suffix = 2; suffix < 10000; suffix++) {
    const candidate = `${directory}${stem} (conflict ${safeLabel} ${stamp} ${suffix})${extension}`;
    if (!exists(candidate)) {
      return candidate;
    }
  }

  throw new Error("Unable to create a unique conflict path.");
}
