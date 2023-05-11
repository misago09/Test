// @ts-nocheck
/* eslint-disable */
/* tslint:disable */
/* prettier-ignore-start */
import React from "react";
import { classNames } from "@plasmicapp/react-web";

export type PasswordIconIconProps = React.ComponentProps<"svg"> & {
  title?: string;
};

export function PasswordIconIcon(props: PasswordIconIconProps) {
  const { className, style, title, ...restProps } = props;
  return (
    <svg
      xmlns={"http://www.w3.org/2000/svg"}
      fill={"none"}
      viewBox={"0 0 24 30"}
      height={"1em"}
      width={"1em"}
      className={classNames("plasmic-default__svg", className)}
      style={style}
      {...restProps}
    >
      {title && <title>{title}</title>}

      <path
        d={
          "M12 0c6.627 0 12 5.373 12 12v16.5a1.5 1.5 0 01-1.5 1.5h-21A1.5 1.5 0 010 28.5V12C0 5.373 5.373 0 12 0zm9.9 15.367H2.1V27.9h19.8V15.368zm-9.358 2.068a2.25 2.25 0 011.042 4.245v3.253a1.05 1.05 0 11-2.1 0l-.001-3.262a2.25 2.25 0 011.059-4.236zM12 2.1c-5.468 0-9.9 4.432-9.9 9.9v1.268h19.8V12c0-5.468-4.432-9.9-9.9-9.9z"
        }
        fill={"currentColor"}
      ></path>
    </svg>
  );
}

export default PasswordIconIcon;
/* prettier-ignore-end */
