/**
 * Semnalul că datele de pe ecran nu sunt (încă) cele cerute.
 *
 * Rapoartele se recalculează la fiecare cerere: pe datele reale, o săptămână ia ~2s, o lună
 * peste 10s. În tot acest timp pagina arăta cifrele intervalului ANTERIOR, fără niciun semn
 * — iar dacă treceai repede prin săptămâni, puteai jura că unele sunt goale. Nu erau.
 *
 * De aceea semnalul e dublu: eticheta intervalului spune „se încarcă", iar conținutul vechi
 * se estompează și nu mai poate fi apăsat. Nu golim pagina: un ecran alb la fiecare click
 * ar fi mai enervant decât util, iar cifrele vechi rămân o referință utilă cât aștepți.
 */

export function RangeLabel({ busy, text }: { busy: boolean; text: string }) {
  return (
    <span className={`range-label${busy ? " busy" : ""}`}>
      {busy ? (
        <>
          <i className="range-spinner" aria-hidden="true" />
          se încarcă…
        </>
      ) : (
        text
      )}
    </span>
  );
}

/** Clasa de pus pe conținutul care încă arată intervalul anterior. */
export const staleClass = (busy: boolean) => (busy ? "stale" : "");
