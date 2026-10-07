/* LAICA UI kit: shadcn/ui's component anatomy, built on Radix primitives (focus, keyboard and positioning done right),
   dressed in Liquid Glass (see ui.css). Copy-and-own components, like shadcn: nothing here talks to the app. */
import * as React from 'react';
import * as DropdownMenuPrimitive from '@radix-ui/react-dropdown-menu';
import * as ContextMenuPrimitive from '@radix-ui/react-context-menu';
import * as PopoverPrimitive from '@radix-ui/react-popover';
import * as DialogPrimitive from '@radix-ui/react-dialog';
import * as TabsPrimitive from '@radix-ui/react-tabs';
import * as SwitchPrimitive from '@radix-ui/react-switch';
import * as SliderPrimitive from '@radix-ui/react-slider';
import * as TooltipPrimitive from '@radix-ui/react-tooltip';
import * as ToggleGroupPrimitive from '@radix-ui/react-toggle-group';
import * as CollapsiblePrimitive from '@radix-ui/react-collapsible';
import * as SeparatorPrimitive from '@radix-ui/react-separator';
import * as ProgressPrimitive from '@radix-ui/react-progress';
import {Command as CommandPrimitive} from 'cmdk';
import {Check,ChevronRight,Search,X,LoaderCircle} from 'lucide-react';

export const cx=(...parts:(string|false|null|undefined)[])=>parts.filter(Boolean).join(' ');
type P<T extends React.ElementType>=React.ComponentPropsWithoutRef<T>;

/* ---------- Dropdown menu ---------- */
export const DropdownMenu=DropdownMenuPrimitive.Root;
export const DropdownMenuTrigger=DropdownMenuPrimitive.Trigger;
export const DropdownMenuGroup=DropdownMenuPrimitive.Group;
export const DropdownMenuSub=DropdownMenuPrimitive.Sub;
export const DropdownMenuContent=React.forwardRef<HTMLDivElement,P<typeof DropdownMenuPrimitive.Content>>(({className,sideOffset=8,...p},ref)=>
  <DropdownMenuPrimitive.Portal><DropdownMenuPrimitive.Content ref={ref} data-lens="" sideOffset={sideOffset} collisionPadding={12} className={cx('ui-pop ui-menu',className)} {...p}/></DropdownMenuPrimitive.Portal>);
export const DropdownMenuItem=React.forwardRef<HTMLDivElement,P<typeof DropdownMenuPrimitive.Item>&{inset?:boolean}>(({className,inset,...p},ref)=><DropdownMenuPrimitive.Item ref={ref} className={cx('ui-item',inset&&'is-inset',className)} {...p}/>);
export const DropdownMenuCheckboxItem=React.forwardRef<HTMLDivElement,P<typeof DropdownMenuPrimitive.CheckboxItem>>(({className,children,...p},ref)=>
  <DropdownMenuPrimitive.CheckboxItem ref={ref} className={cx('ui-item is-inset',className)} {...p}><span className="ui-item-check"><DropdownMenuPrimitive.ItemIndicator><Check size={14}/></DropdownMenuPrimitive.ItemIndicator></span>{children}</DropdownMenuPrimitive.CheckboxItem>);
export const DropdownMenuLabel=React.forwardRef<HTMLDivElement,P<typeof DropdownMenuPrimitive.Label>>(({className,...p},ref)=><DropdownMenuPrimitive.Label ref={ref} className={cx('ui-label',className)} {...p}/>);
export const DropdownMenuSeparator=React.forwardRef<HTMLDivElement,P<typeof DropdownMenuPrimitive.Separator>>(({className,...p},ref)=><DropdownMenuPrimitive.Separator ref={ref} className={cx('ui-sep',className)} {...p}/>);
export const DropdownMenuSubTrigger=React.forwardRef<HTMLDivElement,P<typeof DropdownMenuPrimitive.SubTrigger>>(({className,children,...p},ref)=><DropdownMenuPrimitive.SubTrigger ref={ref} className={cx('ui-item',className)} {...p}>{children}<ChevronRight size={14} className="ui-chev"/></DropdownMenuPrimitive.SubTrigger>);
export const DropdownMenuSubContent=React.forwardRef<HTMLDivElement,P<typeof DropdownMenuPrimitive.SubContent>>(({className,...p},ref)=><DropdownMenuPrimitive.Portal><DropdownMenuPrimitive.SubContent ref={ref} data-lens="" className={cx('ui-pop ui-menu',className)} {...p}/></DropdownMenuPrimitive.Portal>);
export const Shortcut=({className,...p}:React.HTMLAttributes<HTMLSpanElement>)=><span className={cx('ui-shortcut',className)} {...p}/>;

/* ---------- Context menu (right-click) ---------- */
export const ContextMenu=ContextMenuPrimitive.Root;
export const ContextMenuTrigger=ContextMenuPrimitive.Trigger;
export const ContextMenuContent=React.forwardRef<HTMLDivElement,P<typeof ContextMenuPrimitive.Content>>(({className,...p},ref)=><ContextMenuPrimitive.Portal><ContextMenuPrimitive.Content ref={ref} data-lens="" className={cx('ui-pop ui-menu',className)} {...p}/></ContextMenuPrimitive.Portal>);
export const ContextMenuItem=React.forwardRef<HTMLDivElement,P<typeof ContextMenuPrimitive.Item>>(({className,...p},ref)=><ContextMenuPrimitive.Item ref={ref} className={cx('ui-item',className)} {...p}/>);
export const ContextMenuSeparator=React.forwardRef<HTMLDivElement,P<typeof ContextMenuPrimitive.Separator>>(({className,...p},ref)=><ContextMenuPrimitive.Separator ref={ref} className={cx('ui-sep',className)} {...p}/>);

/* ---------- Popover ---------- */
export const Popover=PopoverPrimitive.Root;
export const PopoverTrigger=PopoverPrimitive.Trigger;
export const PopoverAnchor=PopoverPrimitive.Anchor;
export const PopoverContent=React.forwardRef<HTMLDivElement,P<typeof PopoverPrimitive.Content>>(({className,sideOffset=10,align='start',...p},ref)=>
  <PopoverPrimitive.Portal><PopoverPrimitive.Content ref={ref} data-lens="" align={align} sideOffset={sideOffset} collisionPadding={12} className={cx('ui-pop',className)} {...p}/></PopoverPrimitive.Portal>);

/* ---------- Command (cmdk): palettes and searchable pickers ---------- */
export const Command=React.forwardRef<HTMLDivElement,P<typeof CommandPrimitive>>(({className,...p},ref)=><CommandPrimitive ref={ref} className={cx('ui-command',className)} {...p}/>);
export const CommandInput=React.forwardRef<HTMLInputElement,P<typeof CommandPrimitive.Input>>(({className,...p},ref)=><div className="ui-command-input"><Search size={15}/><CommandPrimitive.Input ref={ref} className={className} {...p}/></div>);
export const CommandList=React.forwardRef<HTMLDivElement,P<typeof CommandPrimitive.List>>(({className,...p},ref)=><CommandPrimitive.List ref={ref} className={cx('ui-command-list',className)} {...p}/>);
export const CommandEmpty=React.forwardRef<HTMLDivElement,P<typeof CommandPrimitive.Empty>>(({className,...p},ref)=><CommandPrimitive.Empty ref={ref} className={cx('ui-command-empty',className)} {...p}/>);
export const CommandGroup=React.forwardRef<HTMLDivElement,P<typeof CommandPrimitive.Group>>(({className,...p},ref)=><CommandPrimitive.Group ref={ref} className={cx('ui-command-group',className)} {...p}/>);
export const CommandItem=React.forwardRef<HTMLDivElement,P<typeof CommandPrimitive.Item>>(({className,...p},ref)=><CommandPrimitive.Item ref={ref} className={cx('ui-item',className)} {...p}/>);
export const CommandSeparator=React.forwardRef<HTMLDivElement,P<typeof CommandPrimitive.Separator>>(({className,...p},ref)=><CommandPrimitive.Separator ref={ref} className={cx('ui-sep',className)} {...p}/>);

/* ---------- Dialog and Sheet ---------- */
export const Dialog=DialogPrimitive.Root;
export const DialogTrigger=DialogPrimitive.Trigger;
export const DialogClose=DialogPrimitive.Close;
export const DialogTitle=React.forwardRef<HTMLHeadingElement,P<typeof DialogPrimitive.Title>>(({className,...p},ref)=><DialogPrimitive.Title ref={ref} className={cx('ui-title',className)} {...p}/>);
export const DialogDescription=React.forwardRef<HTMLParagraphElement,P<typeof DialogPrimitive.Description>>(({className,...p},ref)=><DialogPrimitive.Description ref={ref} className={cx('ui-desc',className)} {...p}/>);
export const DialogContent=React.forwardRef<HTMLDivElement,P<typeof DialogPrimitive.Content>&{side?:'center'|'right'|'left';showClose?:boolean;bare?:boolean}>(({className,children,side='center',showClose=true,bare,...p},ref)=>
  <DialogPrimitive.Portal><DialogPrimitive.Overlay className="ui-overlay"/><DialogPrimitive.Content ref={ref} data-lens="" data-side={side} className={cx('ui-pop ui-dialog',bare&&'is-bare',className)} {...p}>{children}{showClose&&<DialogPrimitive.Close className="ui-x" aria-label="Close"><X size={15}/></DialogPrimitive.Close>}</DialogPrimitive.Content></DialogPrimitive.Portal>);

/* ---------- Tabs ---------- */
export const Tabs=TabsPrimitive.Root;
export const TabsList=React.forwardRef<HTMLDivElement,P<typeof TabsPrimitive.List>>(({className,...p},ref)=><TabsPrimitive.List ref={ref} className={cx('ui-tabs',className)} {...p}/>);
export const TabsTrigger=React.forwardRef<HTMLButtonElement,P<typeof TabsPrimitive.Trigger>>(({className,...p},ref)=><TabsPrimitive.Trigger ref={ref} className={cx('ui-tab',className)} {...p}/>);
export const TabsContent=React.forwardRef<HTMLDivElement,P<typeof TabsPrimitive.Content>>(({className,...p},ref)=><TabsPrimitive.Content ref={ref} className={cx('ui-tab-panel',className)} {...p}/>);

/* ---------- Switch, Slider, Toggle group, Progress ---------- */
export const Switch=React.forwardRef<HTMLButtonElement,P<typeof SwitchPrimitive.Root>>(({className,...p},ref)=><SwitchPrimitive.Root ref={ref} className={cx('ui-switch',className)} {...p}><SwitchPrimitive.Thumb className="ui-switch-thumb"/></SwitchPrimitive.Root>);
export const Slider=React.forwardRef<HTMLSpanElement,P<typeof SliderPrimitive.Root>>(({className,...p},ref)=><SliderPrimitive.Root ref={ref} className={cx('ui-slider',className)} {...p}><SliderPrimitive.Track className="ui-slider-track"><SliderPrimitive.Range className="ui-slider-range"/></SliderPrimitive.Track>{(p.value??p.defaultValue??[0]).map((_,i)=><SliderPrimitive.Thumb key={i} className="ui-slider-thumb"/>)}</SliderPrimitive.Root>);
export const ToggleGroup=React.forwardRef<HTMLDivElement,P<typeof ToggleGroupPrimitive.Root>>(({className,...p},ref)=><ToggleGroupPrimitive.Root ref={ref} className={cx('ui-segmented',className)} {...p}/>);
export const ToggleGroupItem=React.forwardRef<HTMLButtonElement,P<typeof ToggleGroupPrimitive.Item>>(({className,...p},ref)=><ToggleGroupPrimitive.Item ref={ref} className={cx('ui-segment',className)} {...p}/>);
export const Progress=React.forwardRef<HTMLDivElement,P<typeof ProgressPrimitive.Root>&{tone?:'ok'|'warn'|'bad'}>(({className,value,tone='ok',...p},ref)=><ProgressPrimitive.Root ref={ref} value={value} className={cx('ui-progress',className)} {...p}><ProgressPrimitive.Indicator className={cx('ui-progress-bar','is-'+tone)} style={{width:`${value??0}%`}}/></ProgressPrimitive.Root>);

/* ---------- Tooltip, Collapsible, Separator ---------- */
export const TooltipProvider=({children}:{children:React.ReactNode})=><TooltipPrimitive.Provider delayDuration={350} skipDelayDuration={200}>{children}</TooltipPrimitive.Provider>;
export function Tip({label,children,side='top',shortcut}:{label:React.ReactNode;children:React.ReactElement;side?:'top'|'right'|'bottom'|'left';shortcut?:string}){
  return <TooltipPrimitive.Root><TooltipPrimitive.Trigger asChild>{children}</TooltipPrimitive.Trigger><TooltipPrimitive.Portal><TooltipPrimitive.Content side={side} sideOffset={8} className="ui-tip">{label}{shortcut&&<kbd className="ui-kbd">{shortcut}</kbd>}</TooltipPrimitive.Content></TooltipPrimitive.Portal></TooltipPrimitive.Root>;
}
export const Collapsible=CollapsiblePrimitive.Root;
export const CollapsibleTrigger=CollapsiblePrimitive.Trigger;
export const CollapsibleContent=CollapsiblePrimitive.Content;
export const Separator=React.forwardRef<HTMLDivElement,P<typeof SeparatorPrimitive.Root>>(({className,...p},ref)=><SeparatorPrimitive.Root ref={ref} className={cx('ui-sep',className)} {...p}/>);

/* ---------- Small display pieces ---------- */
export const Badge=({tone='neutral',className,...p}:React.HTMLAttributes<HTMLSpanElement>&{tone?:'neutral'|'accent'|'ok'|'warn'|'bad'})=><span className={cx('ui-badge','is-'+tone,className)} {...p}/>;
export const Kbd=({className,...p}:React.HTMLAttributes<HTMLElement>)=><kbd className={cx('ui-kbd',className)} {...p}/>;
export const Skeleton=({className,...p}:React.HTMLAttributes<HTMLDivElement>)=><div className={cx('ui-skeleton',className)} {...p}/>;
export const Spinner=({size=16,className}:{size?:number;className?:string})=><LoaderCircle size={size} className={cx('ui-spin',className)} aria-label="Loading"/>;
export const Empty=({icon,title,children}:{icon?:React.ReactNode;title:string;children?:React.ReactNode})=><div className="ui-empty">{icon&&<span className="ui-empty-icon">{icon}</span>}<b>{title}</b>{children&&<p>{children}</p>}</div>;


/** The "this is working" mark: a soft lavender arc that orbits. Paused shows a still pause bar pair instead. */
export const WorkingGlyph=({size=14,paused}:{size?:number;paused?:boolean})=>paused
  ?<span className="work-glyph is-paused" style={{width:size,height:size}} role="img" aria-label="Paused"><i/><i/></span>
  :<span className="work-glyph" style={{width:size,height:size}} role="img" aria-label="Working"/>;