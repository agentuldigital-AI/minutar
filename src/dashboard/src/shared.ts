/**
 * Ce folosesc împreună Dashboard, Total (dispozitive) și Telefon. Stau aici ca aceleași
 * cifre să arate la fel oriunde apar: dacă aceeași durată s-ar rotunji altfel pe două pagini,
 * utilizatorul ar crede că sunt date diferite.
 */

export type Mode = "day" | "week" | "month";
export type ClassName = "productive" | "neutral" | "unproductive";

export const CLASS_LABEL: Record<ClassName, string> = {
  productive: "Productiv",
  neutral: "Neutru",
  unproductive: "Neproductiv",
};

export const CLASS_VAR: Record<ClassName, string> = {
  productive: "var(--cls-productive)",
  neutral: "var(--cls-neutral)",
  unproductive: "var(--cls-unproductive)",
};

export function fmt(s: number): string {
  if (s >= 3600) return `${Math.floor(s / 3600)}h ${Math.round((s % 3600) / 60)}m`;
  if (s >= 60) return `${Math.round(s / 60)}m`;
  return `${Math.round(s)}s`;
}

export function fmtMin(minutes: number): string {
  return fmt(minutes * 60);
}

export function computeRange(mode: Mode, anchor: Date): [Date, Date] {
  const from = new Date(anchor);
  from.setHours(0, 0, 0, 0);
  const to = new Date(from);
  if (mode === "day") {
    to.setDate(to.getDate() + 1);
  } else if (mode === "week") {
    const dow = (from.getDay() + 6) % 7; // Monday-first
    from.setDate(from.getDate() - dow);
    to.setTime(from.getTime());
    to.setDate(to.getDate() + 7);
  } else {
    from.setDate(1);
    to.setTime(from.getTime());
    to.setMonth(to.getMonth() + 1);
  }
  return [from, to];
}

export function rangeLabel(mode: Mode, from: Date, to: Date): string {
  const optsDay: Intl.DateTimeFormatOptions = { day: "numeric", month: "short" };
  if (mode === "day") {
    return from.toLocaleDateString("ro-RO", { weekday: "short", day: "numeric", month: "long" });
  }
  if (mode === "week") {
    const end = new Date(to.getTime() - 1);
    return `${from.toLocaleDateString("ro-RO", optsDay)} – ${end.toLocaleDateString("ro-RO", optsDay)}`;
  }
  return from.toLocaleDateString("ro-RO", { month: "long", year: "numeric" });
}
