export type IconName = 'grid' | 'wallet' | 'calendar' | 'logout';

const svgProps = {
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.65,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
  'aria-hidden': true,
} as const;

export function Icon({ name, className }: { name: IconName; className?: string }) {
  const cls = className ? `icon ${className}` : 'icon';
  switch (name) {
    case 'grid':
      return (
        <svg className={cls} {...svgProps}>
          <rect x={3} y={3} width={7} height={7} rx={1.4} />
          <rect x={14} y={3} width={7} height={7} rx={1.4} />
          <rect x={3} y={14} width={7} height={7} rx={1.4} />
          <rect x={14} y={14} width={7} height={7} rx={1.4} />
        </svg>
      );
    case 'wallet':
      return (
        <svg className={cls} {...svgProps}>
          <path d="M20 8V6a2 2 0 0 0-2-2H6a3 3 0 0 0 0 6h14v10H6a3 3 0 0 1-3-3V7" />
          <path d="M20 12h-5v4h5" />
          <path d="M16 14h.01" />
        </svg>
      );
    case 'calendar':
      return (
        <svg className={cls} {...svgProps}>
          <rect x={3} y={5} width={18} height={16} rx={2} />
          <path d="M16 3v4M8 3v4M3 11h18M8 15h2M14 15h2" />
        </svg>
      );
    case 'logout':
      return (
        <svg className={cls} {...svgProps}>
          <path d="M9 3H4v18h5M13 7l5 5-5 5M8 12h13" />
        </svg>
      );
  }
}
