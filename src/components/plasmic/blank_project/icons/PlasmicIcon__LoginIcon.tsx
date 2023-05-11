// @ts-nocheck
/* eslint-disable */
/* tslint:disable */
/* prettier-ignore-start */
import React from "react";
import { classNames } from "@plasmicapp/react-web";

export type LoginIconIconProps = React.ComponentProps<"svg"> & {
  title?: string;
};

export function LoginIconIcon(props: LoginIconIconProps) {
  const { className, style, title, ...restProps } = props;
  return (
    <svg
      xmlns={"http://www.w3.org/2000/svg"}
      fill={"none"}
      viewBox={"0 0 32 35"}
      height={"1em"}
      width={"1em"}
      className={classNames("plasmic-default__svg", className)}
      style={style}
      {...restProps}
    >
      {title && <title>{title}</title>}

      <path
        d={
          "M5.486 0H32v34.743H5.486v-7.314h2.743V32h21.028V2.743H8.23v4.571H5.486V0zM0 14.629h13.943L6.4 8.229h9.143l10.514 9.142-10.514 9.143H6.4l7.543-6.4H0V14.63z"
        }
        fill={"currentColor"}
      ></path>
    </svg>
  );
}

export default LoginIconIcon;
/* prettier-ignore-end */
