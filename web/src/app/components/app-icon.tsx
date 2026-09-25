export type IconName = 'grid' | 'wallet' | 'calendar' | 'logout' | 'search' | 'back' | 'chevron' | 'info' | 'settings' | 'link' | 'trash' | 'people' | 'shield' | 'plus';

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
    case 'search':
      return (
        <svg className={cls} {...svgProps}>
          <circle cx={10.5} cy={10.5} r={7} />
          <path d="m16 16 5 5" />
        </svg>
      );
    case 'back':
      return (
        <svg className={cls} {...svgProps}>
          <path d="m14 5-7 7 7 7" />
        </svg>
      );
    case 'chevron':
      return (
        <svg className={cls} {...svgProps}>
          <path d="m9 5 7 7-7 7" />
        </svg>
      );
    case 'info':
      return (
        <svg className={cls} {...svgProps}>
          <circle cx={12} cy={12} r={9} />
          <path d="M12 11v6M12 7h.01" />
        </svg>
      );
    case 'settings':
      return (
        <svg className={cls} {...svgProps}>
          <circle cx={12} cy={12} r={3} />
          <path d="M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1" />
        </svg>
      );
    case 'link':
      return (
        <svg className={cls} {...svgProps}>
          <path d="M9 15 15 9" />
          <path d="M11 6l1.5-1.5a4 4 0 1 1 5.5 5.5L16 11" />
          <path d="M13 18l-1.5 1.5a4 4 0 1 1-5.5-5.5L8 13" />
        </svg>
      );
    case 'trash':
      return (
        <svg className={cls} {...svgProps}>
          <path d="M4 7h16" />
          <path d="M10 11v6M14 11v6" />
          <path d="M6 7l1 13a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-13" />
          <path d="M9 7V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v3" />
        </svg>
      );
    case 'people':
      return (
        <svg className={cls} {...svgProps}>
          <circle cx={9} cy={7} r={3} />
          <path d="M3 21v-3a6 6 0 0 1 12 0v3M16 4a3 3 0 0 1 0 6M18 14a5 5 0 0 1 3 4v3" />
        </svg>
      );
    case 'shield':
      return (
        <svg className={cls} {...svgProps}>
          <path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6l8-3" />
          <path d="m8 12 3 3 5-6" />
        </svg>
      );
    case 'plus':
      return (
        <svg className={cls} {...svgProps}>
          <path d="M12 5v14M5 12h14" />
        </svg>
      );
  }
}
